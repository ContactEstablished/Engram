using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary><c>engram status</c> and <c>engram worker start</c>: is the worker up, and where is everything.</summary>
internal static class StatusCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static int Run(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, warnings) = SettingsLoader.Load(home, getEnv);
        var token = WorkerToken.Read(home);
        var (status, body) = WorkerClient.Get(settings.Worker.Port, "/health", Timeout);
        var spooled = Directory.Exists(EngramPaths.Spool(home)) ? Directory.GetFiles(EngramPaths.Spool(home), "*.json").Length : 0;

        output.WriteLine($"home:          {home}");
        output.WriteLine($"worker:        {(status == 200 ? $"running on 127.0.0.1:{settings.Worker.Port} {body}" : $"not running (port {settings.Worker.Port})")}");
        output.WriteLine($"worker binary: {WorkerLauncher.Locate(getEnv) ?? "NOT FOUND"}");
        output.WriteLine($"database:      {(File.Exists(EngramPaths.Database(home)) ? EngramPaths.Database(home) : "not created yet")}");
        output.WriteLine($"token:         {(token is null ? "missing (run engram install)" : "present")}");
        output.WriteLine($"ENGRAM_TOKEN:  {(getEnv(WorkerToken.EnvironmentVariable) is { } env ? env == token ? "set, matches" : "set, DOES NOT MATCH ~/.engram/token" : "not set in this shell")}");
        output.WriteLine($"spooled:       {spooled} payload(s) waiting for the worker");
        if (status == 200 && token is not null && WorkerClient.Get(settings.Worker.Port, "/stats", Timeout, token) is (200, { } stats))
        {
            output.WriteLine($"stored:        {stats}");
        }

        foreach (var warning in warnings)
        {
            output.WriteLine($"warning:       {warning}");
        }

        return status == 200 ? 0 : 1;
    }

    public static int StopWorker(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        var status = WorkerClient.Post(settings.Worker.Port, "/shutdown", WorkerToken.Read(home), [], Timeout);
        output.WriteLine(status switch
        {
            null => "Worker not running.",
            200 => "Worker stopping.",
            _ => $"Worker refused to stop (HTTP {status}); is ~/.engram/token the one it started with?",
        });
        return status is null or 200 ? 0 : 1;
    }

    public static int StartWorker(Func<string, string?> getEnv, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        if (WorkerClient.Get(settings.Worker.Port, "/health", Timeout).Status == 200)
        {
            output.WriteLine("Worker already running.");
            return 0;
        }

        if (WorkerLauncher.Locate(getEnv) is not { } worker)
        {
            output.WriteLine($"engram-worker not found next to {AppContext.BaseDirectory}");
            return 1;
        }

        if (!WorkerLauncher.TryStart(worker, home, out var error))
        {
            output.WriteLine($"Could not start the worker: {error}");
            return 1;
        }

        for (var i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            if (WorkerClient.Get(settings.Worker.Port, "/health", Timeout).Status == 200)
            {
                output.WriteLine($"Worker started on 127.0.0.1:{settings.Worker.Port}.");
                return 0;
            }
        }

        output.WriteLine($"Worker didn't answer within 5 s; see {EngramPaths.Logs(home)}.");
        return 1;
    }
}
