using System.Text.Json;
using System.Text.RegularExpressions;
using Engram.Core;
using Engram.Core.Hooks;
using Engram.Core.Security;
using Engram.Core.Settings;
using Engram.Worker.Storage;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Engram.Worker.Capture;

internal enum CaptureOutcome
{
    Stored,
    Ignored,          // an event Engram doesn't use
    Disabled,         // capture off (settings or repo .engram.json)
    ObserverRun,      // from Engram's own summarizer
    SubagentHandback, // UserPromptSubmit that is really a subagent's report
    ToolFiltered,     // not in the matcher, or in the skip list
    ExcludedPath,
}

/// <summary>
/// Applies the "before anything is stored" rules and writes the result. Runs on the background writer,
/// never on the hook's request path.
/// </summary>
internal sealed class CaptureProcessor
{
    private const string AgentMessagePrefix = "<agent-message";
    private static readonly string[] PathKeys = ["file_path", "path", "notebook_path"];

    private readonly CaptureStore _store;
    private readonly ProjectResolver _projects;
    private readonly TimeProvider _time;
    private readonly CaptureSettings _settings;
    private readonly PayloadShaper _shaper;
    private readonly Regex _toolMatcher;
    private readonly HashSet<string> _skipTools;
    private readonly Matcher _excluded;
    private readonly string _observerSandbox;
    private readonly string _platform;

    public CaptureProcessor(CaptureStore store, ProjectResolver projects, TimeProvider time, WorkerContext context, ILogger<CaptureProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        _store = store;
        _projects = projects;
        _time = time;
        _settings = context.Settings.Capture;
        var redactor = new Redactor(_settings.RedactPatterns);
        foreach (var invalid in redactor.InvalidPatterns)
        {
            logger.LogWarning("capture.redactPatterns: invalid regex skipped: {Pattern}", invalid);
        }

        _shaper = new PayloadShaper(redactor, _settings.MaxStringBytes, _settings.MaxPromptBytes);
        _toolMatcher = BuildMatcher(_settings.ToolMatcher, logger);
        _skipTools = new HashSet<string>(_settings.SkipTools, StringComparer.Ordinal);
        _excluded = new Matcher(StringComparison.OrdinalIgnoreCase);
        _excluded.AddIncludePatterns(_settings.ExcludePaths);
        _observerSandbox = ProjectResolver.NormalizePath(EngramPaths.ObserverSandbox(context.Home));
        _platform = context.Platform;
    }

    public CaptureOutcome Process(HookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var at = _time.GetUtcNow();
        var cwd = ProjectResolver.NormalizePath(payload.Cwd);
        if (cwd == _observerSandbox || cwd.StartsWith(_observerSandbox + "/", StringComparison.Ordinal))
        {
            return CaptureOutcome.ObserverRun;
        }

        if (!_settings.Enabled)
        {
            return CaptureOutcome.Disabled;
        }

        var outcome = Prefilter(payload);
        if (outcome != CaptureOutcome.Stored)
        {
            return outcome;
        }

        var project = _projects.Resolve(payload.Cwd);
        if (project.Worktree is { } root && SettingsLoader.LoadRepo(root)?.Capture?.Enabled == false)
        {
            return CaptureOutcome.Disabled;
        }

        EnsureSession(payload, project, at, payload as SessionStartPayload);
        switch (payload)
        {
            case SessionStartPayload:
                break; // EnsureSession recorded it
            case UserPromptSubmitPayload p:
                _store.InsertPrompt(p.SessionId, p.PromptId, _shaper.ShapeLongText(p.Prompt), at);
                break;
            case PostToolUsePayload p:
                _store.InsertEvent(new EventInsert(
                    p.SessionId, p.PromptId, p.HookEventName, p.ToolName, p.ToolUseId, p.AgentId,
                    _shaper.ShapeInput(p.ToolInput), _shaper.ShapeResponse(p.ToolResponse), null,
                    IsError: false, ToLong(p.DurationMs), at));
                break;
            case PostToolUseFailurePayload p:
                _store.InsertEvent(new EventInsert(
                    p.SessionId, p.PromptId, p.HookEventName, p.ToolName, p.ToolUseId, p.AgentId,
                    _shaper.ShapeInput(p.ToolInput), null, _shaper.ShapeText(p.Error),
                    IsError: true, ToLong(p.DurationMs), at));
                break;
            case StopPayload p:
                // Stop has no tool_use_id; key it on the turn so a retried hook is stored once.
                _store.InsertEvent(new EventInsert(
                    p.SessionId, p.PromptId, p.HookEventName, null, p.PromptId is { } turn ? $"stop:{p.SessionId}:{turn}" : null, p.AgentId, null, null,
                    p.LastAssistantMessage is { } m ? _shaper.ShapeLongText(m) : null,
                    IsError: false, null, at));
                _store.EnqueueJob(JobKinds.ObserveTurn, p.SessionId, p.PromptId, at);
                break;
            case PreCompactPayload p:
                _store.EnqueueJob(JobKinds.SummarizeCheckpoint, p.SessionId, p.PromptId, at);
                break;
            case SessionEndPayload p:
                _store.EndSession(p.SessionId, p.Reason, at);
                _store.EnqueueJob(JobKinds.SummarizeSession, p.SessionId, null, at);
                break;
            default:
                return CaptureOutcome.Ignored;
        }

        return CaptureOutcome.Stored;
    }

    /// <summary>Cheap checks that don't need git or the database.</summary>
    private CaptureOutcome Prefilter(HookPayload payload) => payload switch
    {
        SessionStartPayload or StopPayload or PreCompactPayload or SessionEndPayload => CaptureOutcome.Stored,
        UserPromptSubmitPayload p when p.Prompt.TrimStart().StartsWith(AgentMessagePrefix, StringComparison.Ordinal) => CaptureOutcome.SubagentHandback,
        UserPromptSubmitPayload => CaptureOutcome.Stored,
        PostToolUsePayload p => FilterTool(p.ToolName, p.ToolInput, p.Cwd),
        PostToolUseFailurePayload p => FilterTool(p.ToolName, p.ToolInput, p.Cwd),
        _ => CaptureOutcome.Ignored,
    };

    private CaptureOutcome FilterTool(string toolName, JsonElement input, string cwd)
    {
        if (_skipTools.Contains(toolName) || !_toolMatcher.IsMatch(toolName))
        {
            return CaptureOutcome.ToolFiltered;
        }

        return TouchesExcludedPath(input, cwd) ? CaptureOutcome.ExcludedPath : CaptureOutcome.Stored;
    }

    private bool TouchesExcludedPath(JsonElement input, string cwd)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in PathKeys)
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path)
            {
                // Match relative to the cwd when possible, and on the bare file name otherwise, so
                // "**/.env*" catches C:\anything\.env.local.
                var normalized = ProjectResolver.NormalizePath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
                var relative = normalized.StartsWith(ProjectResolver.NormalizePath(cwd) + "/", StringComparison.Ordinal)
                    ? normalized[(ProjectResolver.NormalizePath(cwd).Length + 1)..]
                    : normalized.TrimStart('/').Replace(":", string.Empty, StringComparison.Ordinal);
                if (_excluded.Match(relative).HasMatches || _excluded.Match(Path.GetFileName(relative)).HasMatches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EnsureSession(HookPayload payload, ProjectInfo project, DateTimeOffset at, SessionStartPayload? start)
    {
        var projectId = _store.GetOrCreateProject(project.Identity, project.Name, at);
        _store.UpsertSession(new SessionUpsert(
            payload.SessionId, projectId, _platform, payload.Cwd, project.Worktree, project.Branch,
            start?.Model, start?.Source, payload.TranscriptPath, at));
    }

    private static long? ToLong(double? value) => value is { } v ? (long)Math.Round(v) : null;

    private static Regex BuildMatcher(string pattern, ILogger logger)
    {
        try
        {
            return new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            logger.LogWarning("capture.toolMatcher is not a valid regex; using the default.");
            return new Regex($"^(?:{new CaptureSettings().ToolMatcher})$", RegexOptions.CultureInvariant);
        }
    }
}
