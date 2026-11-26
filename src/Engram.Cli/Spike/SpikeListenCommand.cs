using System.Globalization;
using Engram.Core;

namespace Engram.Cli.Spike;

/// <summary><c>engram spike listen [--port 47613]</c>. Runs until Ctrl+C.</summary>
internal static class SpikeListenCommand
{
    public const int DefaultPort = 47613;

    public static int Run(string[] args)
    {
        var port = DefaultPort;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
            {
                port = p;
                i++;
            }
            else
            {
                throw new ArgumentException($"spike listen: unexpected argument '{args[i]}'. Usage: engram spike listen [--port N]");
            }
        }

        var spikeDir = EngramPaths.SpikeDir(Environment.GetEnvironmentVariable);
        var token = Environment.GetEnvironmentVariable("ENGRAM_TOKEN");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var listener = new SpikeListener(spikeDir, token, Console.Out);
        listener.Start(port);

        Console.WriteLine($"engram spike listen: http://127.0.0.1:{listener.Port}/  (logging to {spikeDir})");
        Console.WriteLine(string.IsNullOrEmpty(token)
            ? "  ENGRAM_TOKEN is not set in this shell, so authorization can't be compared (matches_env = null)."
            : $"  Comparing Authorization against this shell's ENGRAM_TOKEN ({token.Length} chars).");
        Console.WriteLine("  Ctrl+C to stop.");

        listener.RunAsync(cts.Token).GetAwaiter().GetResult();
        return 0;
    }
}
