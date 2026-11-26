using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Engram.Core;
using Engram.Core.Settings;

namespace Engram.Worker.Observer;

internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, string StdIn);

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs a process with stdin and a timeout. Swappable so the provider can be tested without Claude.</summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in spec.Environment)
        {
            psi.Environment[name] = value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), cts.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

/// <summary>
/// Runs the unmodified <c>claude -p</c> binary (never reads Claude Code's credentials). The call is kept tiny:
/// no tools, no MCP servers (they add ~65k tokens of context per call), no user settings or plugins, no saved
/// session, the sandbox folder as cwd and <c>ENGRAM_DISABLE=1</c> so Engram's own hooks ignore the run.
/// </summary>
internal sealed class ClaudeCliClient(ObserverSettings settings, string home, IProcessRunner runner, Func<string, string?> getEnv) : ILlmClient
{
    public string Provider => ObserverSettings.ClaudeCli;

    public string Model => settings.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exe = Locate(settings.ClaudePath, getEnv) ?? throw new LlmException("claude was not found on PATH; set observer.claudePath in settings.json");
        var sandbox = EngramPaths.ObserverSandbox(home);
        Directory.CreateDirectory(sandbox);

        var spec = new ProcessSpec(
            exe,
            [
                "-p",
                "--tools", string.Empty,
                "--model", settings.Model,
                "--output-format", "json",
                "--no-session-persistence",
                "--disable-slash-commands",
                "--strict-mcp-config",
                "--setting-sources", "local",
                "--system-prompt", request.System,
            ],
            sandbox,
            new Dictionary<string, string> { ["ENGRAM_DISABLE"] = "1" },
            request.User);

        var started = Stopwatch.GetTimestamp();
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(spec, TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"claude timed out after {settings.TimeoutSeconds}s", Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new LlmException("could not run claude: " + ex.Message, Stopwatch.GetElapsedTime(started), ex);
        }

        var latency = Stopwatch.GetElapsedTime(started);
        return Parse(result, latency);
    }

    /// <summary>Reads claude's <c>--output-format json</c> envelope. Only the fields we need are kept; nothing else is stored or logged.</summary>
    internal static LlmResponse Parse(ProcessResult result, TimeSpan latency)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.StdOut);
        }
        catch (JsonException)
        {
            throw new LlmException($"claude exited {result.ExitCode} without JSON output: {Snippet(result.StdErr.Length > 0 ? result.StdErr : result.StdOut)}", latency);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (result.ExitCode != 0 || isError || text is null)
            {
                throw new LlmException($"claude failed (exit {result.ExitCode}): {Snippet(text ?? result.StdErr)}", latency);
            }

            long? Long(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
            var usage = root.TryGetProperty("usage", out var u) ? u : default;
            var cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var d) ? d : (double?)null;
            var model = root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
                ? mu.EnumerateObject().Select(p => p.Name).FirstOrDefault()
                : null;
            return new LlmResponse(
                text,
                new LlmUsage(Long(usage, "input_tokens"), Long(usage, "output_tokens"), Long(usage, "cache_read_input_tokens"), Long(usage, "cache_creation_input_tokens"), cost),
                latency,
                model ?? "claude");
        }
    }

    internal static string? Locate(string? configured, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(getEnv);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (getEnv("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // The native installer's location, for a worker started with a minimal PATH.
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
        return File.Exists(local) ? local : null;
    }

    private static string Snippet(string text)
    {
        var t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 200 ? t : t[..200] + "…";
    }
}
