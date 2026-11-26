using System.Globalization;
using System.Text;
using Engram.Cli.Spike;
using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Hooks;

/// <param name="Stdin">Hook JSON from Claude Code.</param>
/// <param name="Stdout">Becomes session context: the memory index the worker returns.</param>
/// <param name="GetEnv">Environment lookup.</param>
/// <param name="FetchContext">POSTs the payload to the worker's /context; returns status and body (nulls if unreachable).</param>
/// <param name="StartWorker">Starts the worker and waits for it; returns an error message or null.</param>
internal sealed record SessionStartContext(
    Stream Stdin,
    Stream Stdout,
    Func<string, string?> GetEnv,
    Func<int, string, byte[], (int? Status, string? Body)> FetchContext,
    Func<int, string, string?> StartWorker);

/// <summary>
/// <c>engram hook session-start</c>: registers the session with the worker and prints the memory index for
/// this repo, which Claude Code adds to the session's context. Starts the worker if it isn't running and
/// waits briefly for it, since the first session after a reboot is when the memory matters most. Fails open:
/// always exits 0, prints nothing but the context, and gives up after a few seconds.
/// </summary>
internal static class SessionStartHook
{
    public const string ContextPath = "/context";
    public static readonly TimeSpan ContextTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ColdStartWait = TimeSpan.FromSeconds(3);
    private const int MaxStdinBytes = 8 * 1024 * 1024;

    public static SessionStartContext Default(Func<string, string?> getEnv) => new(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        getEnv,
        (port, token, body) => WorkerClient.PostForBody(port, ContextPath, token, body, ContextTimeout),
        (port, home) => WorkerLauncher.EnsureRunning(port, home, getEnv, ColdStartWait));

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
            var port = settings.Worker.Port;
            var token = WorkerToken.Read(home);
            if (token is null)
            {
                // Not installed yet: the worker creates the token on first start; keep the payload for it.
                Spool(home, body);
                if (ctx.StartWorker(port, home) is { } startError)
                {
                    LogError(home, $"session-start: could not start the worker: {startError}");
                }

                return 0;
            }

            var (status, context) = ctx.FetchContext(port, token, body);
            if (status is null)
            {
                // Worker down: start it, wait for it, and try once more.
                if (ctx.StartWorker(port, home) is { } startError)
                {
                    Spool(home, body);
                    LogError(home, $"session-start: could not start the worker: {startError}; payload spooled");
                    return 0;
                }

                (status, context) = ctx.FetchContext(port, token, body);
            }

            if (status is >= 200 and < 300)
            {
                WriteContext(ctx.Stdout, context);
                return 0;
            }

            Spool(home, body);
            LogError(home, string.Create(CultureInfo.InvariantCulture, $"session-start: worker answered {status?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}; payload spooled"));
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

    /// <summary>Raw UTF-8 bytes (never Console.Out, which uses the console code page on Windows).</summary>
    private static void WriteContext(Stream stdout, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return;
        }

        stdout.Write(Encoding.UTF8.GetBytes(context));
        stdout.Flush();
    }

    /// <summary>Writes atomically (temp file then rename) so the worker never reads half a payload.</summary>
    internal static void Spool(string home, byte[] body)
    {
        var dir = EngramPaths.Spool(home);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var temp = Path.Combine(dir, name + ".tmp");
        File.WriteAllBytes(temp, body);
        File.Move(temp, Path.Combine(dir, name + ".json"));
    }

    private static void LogError(string home, string message)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            SafeFile.Append(Path.Combine(EngramPaths.Logs(home), "hook-errors.log"), Encoding.UTF8.GetBytes(line));
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
