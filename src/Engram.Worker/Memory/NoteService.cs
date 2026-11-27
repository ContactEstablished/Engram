using System.Globalization;
using System.Text;
using System.Text.Json;
using Engram.Core;
using Engram.Core.Projects;
using Engram.Core.Security;
using Engram.Worker.Capture;
using Engram.Worker.Embedding;
using Engram.Worker.Observer;
using Engram.Worker.Storage;

namespace Engram.Worker.Memory;

/// <summary>
/// A note from <c>engram save</c>, as the CLI sends it (live, or through <c>~/.engram/spool/notes</c>). The CLI
/// resolves <see cref="Project"/> with the shared <see cref="ProjectIdentity"/> when the note is saved, so a spooled
/// note lands in the right project even if its folder is gone by the time the worker reads it.
/// </summary>
internal sealed record NoteRequest(string? ClientId, string? Text, string? Folder, string? Title, string? Type, string? Source, DateTimeOffset? SavedAt, ProjectInfo? Project)
{
    /// <summary>Reads the JSON body. Throws <see cref="JsonException"/> when it isn't an object.</summary>
    public static NoteRequest Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("expected a JSON object");
        }

        static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        ProjectInfo? project = null;
        if (root.TryGetProperty("project", out var p) && Str(p, "identity") is { Length: > 0 } identity)
        {
            project = new ProjectInfo(identity, Str(p, "name") ?? identity, Str(p, "worktree"), Str(p, "branch"));
        }

        DateTimeOffset? savedAt = DateTimeOffset.TryParse(Str(root, "saved_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new NoteRequest(Str(root, "client_id"), Str(root, "text"), Str(root, "folder"), Str(root, "title"), Str(root, "type"), Str(root, "source"), savedAt, project);
    }
}

/// <summary>What was stored. <see cref="Summarized"/> is <c>queued</c>: the summarizer replaces the provisional narrative later.</summary>
internal sealed record NoteSaved(long Id, bool Existing, string Project, string Type, string Title, string Summarized, IReadOnlyList<string> Warnings);

/// <summary>Validates and stores notes. Both the endpoint and the spool ingestor go through here.</summary>
internal sealed class NoteService(
    WorkerContext context,
    MemoryStore store,
    CaptureStore capture,
    ProjectResolver projects,
    ObserverRuntime observer,
    JobSignal jobs,
    EmbeddingSignal embeddings,
    TimeProvider time)
{
    /// <summary>The largest note, in UTF-8 bytes. The CLI checks it too, before anything is sent or spooled.</summary>
    public const int MaxBytes = 256 * 1024;

    public const int MaxTitle = 120;
    public const int MaxSource = 200;
    private const int ProvisionalNarrative = 700;

    /// <summary>Stores the note, or returns why it can't be stored (a caller error: nothing is written).</summary>
    public (NoteSaved? Saved, string? Error) Save(NoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, "the note is empty");
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the note is over the {MaxBytes / 1024} KiB limit ({MaxBytes} bytes of UTF-8)"));
        }

        if (string.IsNullOrWhiteSpace(request.Folder))
        {
            return (null, "the note needs the project folder");
        }

        string? givenType = null;
        if (request.Type is { Length: > 0 } && (givenType = ObservationTypes.Normalize(request.Type)) is null)
        {
            return (null, $"unknown type '{request.Type}'; allowed: {string.Join(", ", ObservationTypes.All)}");
        }

        var project = request.Project;
        if (project is null)
        {
            if (!Directory.Exists(request.Folder))
            {
                return (null, $"{request.Folder} is not a folder");
            }

            project = projects.Resolve(request.Folder);
        }

        // Same redaction as captured turns, before anything is stored or sent to the summarizer.
        var redactor = new Redactor(context.Settings.Capture.RedactPatterns);
        var redacted = redactor.Redact(text);
        var givenTitle = request.Title?.Trim() is { Length: > 0 } t ? Clip(redactor.Redact(OneLine(t)), MaxTitle) : null;
        var source = request.Source?.Trim() is { Length: > 0 } s ? Clip(redactor.Redact(OneLine(s)), MaxSource) : null;
        var type = givenType ?? "discovery";
        var title = givenTitle ?? Clip(ProvisionalTitle(redacted), MaxTitle);
        var provisional = new Observation(type, title, Clip(redacted, ProvisionalNarrative), [], []);
        var now = time.GetUtcNow();
        var projectId = capture.GetOrCreateProject(project.Identity, project.Name, now);
        var createdAt = request.SavedAt is { } at && at <= now ? at : now;
        var (id, existing) = store.SaveNote(
            projectId,
            context.Platform,
            request.Folder,
            project.Branch,
            provisional,
            new NoteText(redacted, source, givenTitle is not null, givenType is not null, request.ClientId is { Length: > 0 and <= 64 } c ? c : null),
            createdAt);
        if (!existing)
        {
            jobs.Notify();
            embeddings.Notify();
        }

        List<string> warnings = observer.Observer is null
            ? [$"the summarizer is off (observer.provider = {context.Settings.Observer.Provider}); the note is stored and searchable, and will be summarized once a provider is configured"]
            : [];
        return (new NoteSaved(id, existing, project.Identity, type, title, "queued", warnings), null);
    }

    /// <summary>The endpoint's (and the CLI's) JSON for a stored note.</summary>
    public static string ToJson(NoteSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", saved.Id);
            w.WriteString("project", saved.Project);
            w.WriteString("type", saved.Type);
            w.WriteString("title", saved.Title);
            w.WriteString("summarized", saved.Summarized);
            w.WriteBoolean("existing", saved.Existing);
            w.WriteStartArray("warnings");
            foreach (var warning in saved.Warnings)
            {
                w.WriteStringValue(warning);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The title a note has until its summary arrives: the first sentence of its first non-blank line, without
    /// Markdown heading or list marks (rulings tend to start with "# Arbiter ruling: ...").
    /// </summary>
    internal static string ProvisionalTitle(string text)
    {
        var first = text.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Trim().TrimStart('#', '*', '-', '>', ' '))
            .FirstOrDefault(l => l.Length > 0) ?? text;
        return MemoryEndpoints.DeriveTitle(first);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
