using System.Diagnostics;
using Engram.Cli.Spike;

namespace Engram.Cli;

internal static class Program
{
    private const string Usage = """
        engram — personal memory service for Claude Code (Phase 0 spike)

        Usage:
          engram spike capture                      Hook handler: append stdin hook JSON to ~/.engram/spike/<event>.jsonl
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
