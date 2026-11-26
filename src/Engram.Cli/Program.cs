using System.Diagnostics;
using Engram.Cli.Commands;
using Engram.Cli.Hooks;
using Engram.Cli.Mcp;
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
          engram enable [FOLDER...] [--worktrees] [--mcp local|file|none] [--dry-run] [--yes]
                                                    Turn Engram on for projects (merges hooks into .claude/settings.local.json)
          engram disable [FOLDER...]                Turn it off again (removes only Engram's entries)
          engram recent [--limit N]                 Show the latest observations and session summaries
          engram search WORDS [--all] [--keyword|--semantic] [--type T] [--since 14d] [--limit N]
                                                    Search memory of the current repo (or all repos)
          engram show ID...                         Full records for observations
          engram eval FILE [--k 5]                  Retrieval eval: recall of keyword vs meaning vs hybrid search
          engram mcp                                MCP server (stdio) with search, timeline, get_observations, save_memory, forget
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
                ["recent", .. var recentArgs] => RecentCommand.Run(recentArgs, Environment.GetEnvironmentVariable, Console.Out),
                ["enable", .. var enableArgs] => EnableCommand.Run(true, enableArgs, Environment.GetEnvironmentVariable, Console.Out, Console.In),
                ["disable", .. var disableArgs] => EnableCommand.Run(false, disableArgs, Environment.GetEnvironmentVariable, Console.Out, Console.In),
                ["search", .. var searchArgs] => SearchCommand.Search(searchArgs, new WorkerMemoryApi(Environment.GetEnvironmentVariable), McpCwd, Console.Out),
                ["show", .. var showArgs] => SearchCommand.Show(showArgs, new WorkerMemoryApi(Environment.GetEnvironmentVariable), McpCwd, Console.Out),
                ["eval", .. var evalArgs] => EvalCommand.Run(evalArgs, new WorkerMemoryApi(Environment.GetEnvironmentVariable), McpCwd, Console.Out),
                ["mcp"] => RunMcp(),
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

    /// <summary>The repo the session is about: Claude Code's project dir if it says, else our working directory.</summary>
    private static string McpCwd() => Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;

    private static int RunMcp()
    {
        var api = new WorkerMemoryApi(Environment.GetEnvironmentVariable);
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        new McpServer(EngramTools.Create(api, McpCwd), version, EngramTools.Instructions)
            .Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
        return 0;
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
