using System.Globalization;
using System.Text;
using Engram.Cli.Spike;
using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Hooks;

/// <param name="Stdin">Hook JSON from Claude Code.</param>
/// <param name="Stdout">Becomes session context. Phase 1 writes nothing; Phase 3 injects the memory index.</param>
/// <param name="GetEnv">Environment lookup.</param>
/// <param name="Post">Sends the payload to the worker; returns the HTTP status or null. Swappable for tests.</param>
/// <param name="StartWorker">Starts the worker; returns an error message or null.</param>
internal sealed record SessionStartContext(
    Stream Stdin,
    Stream Stdout,
    Func<string, string?> GetEnv,
    Func<int, string?, byte[], int?> Post,
    Func<string, string?> StartWorker);

/// <summary>
/// <c>engram hook session-start</c>: registers the session with the worker, starting the worker if it
/// isn't running. SessionStart can't use HTTP hooks, so this command is the bridge. Fails open: always
/// exits 0, never prints errors, and never waits more than a few hundred milliseconds.
/// </summary>
internal static class SessionStartHook
{
    public const string Path = "/hooks/session-start";
    public static readonly TimeSpan PostTimeout = TimeSpan.FromMilliseconds(250);
    private const int MaxStdinBytes = 8 * 1024 * 1024;

    public static SessionStartContext Default(Func<string, string?> getEnv) => new(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        getEnv,
        (port, token, body) => WorkerClient.Post(port, Path, token, body, PostTimeout),
        home => WorkerLauncher.Locate(getEnv) is { } worker
            ? WorkerLauncher.TryStart(worker, home, out var error) ? null : error
            : $"engram-worker not found next to {AppContext.BaseDirectory} (set {WorkerLauncher.PathVariable})");

    public static int Run(SessionStartContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.GetEnv("ENGRAM_DISABLE") == "1")
        {
            return 0;
        }

        string? home = null;
        try
        {
            home = EngramPaths.Home(ctx.GetEnv);
            var body = ReadAll(ctx.Stdin);
            if (body.Length == 0)
            {
                return 0;
            }

            var (settings, _) = SettingsLoader.Load(home, ctx.GetEnv);
            var token = WorkerToken.Read(home);
            var status = token is null ? null : ctx.Post(settings.Worker.Port, token, body);
            if (status is >= 200 and < 300)
            {
                return 0;
            }

            // Worker down or still starting: keep the payload for it, and start it.
            Spool(home, body);
            if (status is null && ctx.StartWorker(home) is { } error)
            {
                LogError(home, $"session-start: could not start the worker: {error}");
            }
            else if (status is not null)
            {
                LogError(home, string.Create(CultureInfo.InvariantCulture, $"session-start: worker answered {status}; payload spooled"));
            }
        }
        catch (Exception ex)
        {
            if (home is not null)
            {
                LogError(home, "session-start: " + ex);
            }
        }

        return 0;
    }

    /// <summary>Writes atomically (temp file then rename) so the worker never reads half a payload.</summary>
    internal static void Spool(string home, byte[] body)
    {
        var dir = EngramPaths.Spool(home);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var temp = System.IO.Path.Combine(dir, name + ".tmp");
        File.WriteAllBytes(temp, body);
        File.Move(temp, System.IO.Path.Combine(dir, name + ".json"));
    }

    private static void LogError(string home, string message)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            SafeFile.Append(System.IO.Path.Combine(EngramPaths.Logs(home), "hook-errors.log"), Encoding.UTF8.GetBytes(line));
        }
        catch
        {
            // Fail open, even when the log can't be written.
        }
    }

    private static byte[] ReadAll(Stream stdin)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stdin.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxStdinBytes)
            {
                throw new InvalidDataException("SessionStart payload larger than 8 MB.");
            }
        }

        // Whitespace-only input counts as empty.
        var bytes = buffer.ToArray();
        return bytes.AsSpan().Trim(" \t\r\n"u8).IsEmpty ? [] : bytes;
    }
}
