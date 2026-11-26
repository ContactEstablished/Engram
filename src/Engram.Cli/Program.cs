using System.Diagnostics;
using Engram.Cli.Commands;
using Engram.Cli.Hooks;
using Engram.Cli.Spike;

namespace Engram.Cli;

internal static class Program
{
    private const string Usage = """
        engram — personal memory service for Claude Code

        Usage:
          engram install [--yes] [--rotate-token] [--no-env]
                                                    Create ~/.engram, default settings and the worker token; export ENGRAM_TOKEN
          engram status                             Show whether the worker is running and where things are
          engram worker start | stop                Start the worker in the background, or stop it
          engram hook session-start                 SessionStart hook handler (registers the session, starts the worker)

        Phase 0 spike tools:
          engram spike capture                     Hook handler: append stdin hook JSON to ~/.engram/spike/<event>.jsonl
          engram spike listen [--port 47613]        Log HTTP hook POSTs (bodies + headers) to ~/.engram/spike/http.<event>.jsonl
          engram spike fixtures [--from DIR]... [--out DIR] [--redact TERM]...
                                                    Write sanitized fixtures to tests/fixtures/hooks
          engram --version

        Environment:
          ENGRAM_HOME     Data folder (default ~/.engram)
          ENGRAM_DISABLE  When 1, hook handlers exit immediately
        """;

    private static int Main(string[] args)
    {
        var start = Stopwatch.GetTimestamp();

        // Hot path first: hook handlers must stay fast and silent.
        if (args is ["hook", "session-start", ..])
        {
            return SessionStartHook.Run(SessionStartHook.Default(Environment.GetEnvironmentVariable));
        }

        if (args is ["spike", "capture", ..])
        {
            return SpikeCapture.Run(new CaptureContext(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                Console.OpenStandardError(),
                Environment.GetEnvironmentVariable,
                start,
                ProcessUptimeMs));
        }

        try
        {
            return args switch
            {
                ["install", .. var rest] => InstallCommand.Run(rest),
                ["status"] => StatusCommand.Run(Environment.GetEnvironmentVariable, Console.Out),
                ["worker", "start"] => StatusCommand.StartWorker(Environment.GetEnvironmentVariable, Console.Out),
                ["worker", "stop"] => StatusCommand.StopWorker(Environment.GetEnvironmentVariable, Console.Out),
                ["spike", "listen", .. var rest] => SpikeListenCommand.Run(rest),
                ["spike", "fixtures", .. var rest] => SpikeFixturesCommand.Run(rest),
                ["--version"] => PrintVersion(),
                ["--help"] or ["-h"] or ["help"] => PrintUsage(Console.Out, 0),
                _ => PrintUsage(Console.Error, 1),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"engram: {ex.Message}");
            return 1;
        }
    }

    private static double? ProcessUptimeMs()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return (DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static int PrintVersion()
    {
        Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        return 0;
    }

    private static int PrintUsage(TextWriter writer, int exitCode)
    {
        writer.WriteLine(Usage);
        return exitCode;
    }
}
