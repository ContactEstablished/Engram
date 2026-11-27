using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Engram.Cli.Commands;
using Engram.Core;
using Engram.Tests.Observer;
using Engram.Tests.Worker;
using Engram.Worker.Capture;
using Microsoft.Extensions.DependencyInjection;

namespace Engram.Tests.Cli;

/// <summary><c>engram save</c>: the contract Chorus calls after a Council run.</summary>
public sealed class SaveCommandTests : IDisposable
{
    private const string Summary = """{"observations":[{"type":"decision","title":"Chose X over Y","narrative":"X won because of Z.","facts":[],"files":[]}]}""";

    private readonly FakeLlmClient _llm = new();
    private readonly WorkerFactory _factory;
    private readonly Func<string, string?> _env;

    public SaveCommandTests()
    {
        _factory = new WorkerFactory("""{ "observer": { "provider": "claude-cli" }, "search": { "semantic": false } }""", _llm);
        _ = _factory.Services; // start the in-process worker so its database exists, even in tests that play a stopped one
        _env = name => name switch
        {
            "ENGRAM_HOME" => _factory.Home,
            "ENGRAM_PORT" => "47991", // nothing listens here
            _ => null,
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    /// <summary>Sends to the in-process worker, or plays a stopped one.</summary>
    private sealed class FactoryTransport(WorkerFactory factory, bool running) : INoteTransport
    {
        public bool WorkerRunning() => running;

        public (int? Status, string? Body) Send(byte[] json)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token);
            using var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = client.PostAsync(new Uri("/memory/note", UriKind.Relative), content).GetAwaiter().GetResult();
            return ((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
    }

    /// <summary>A repo in a folder whose path has spaces.</summary>
    private string Repo()
    {
        var dir = _factory.Temp.Combine("My Projects", "Sample App");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "remote", "add", "origin", "git@github.com:Example/Sample-App.git");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        Git(dir, "add", "a.txt");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
        return dir;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, p.StandardError.ReadToEnd());
    }

    private (int Exit, JsonElement Json, string Stdout, string Stderr) Save(string stdin, INoteTransport? transport, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        var exit = SaveCommand.Run(args, _env, input, stdinIsTerminal: false, stdout, stderr, transport, TimeProvider.System);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var json = args.Contains("--json") ? JsonDocument.Parse(lines.Single()).RootElement.Clone() : default;
        return (exit, json, stdout.ToString(), stderr.ToString());
    }

    private async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        return condition();
    }

    [Fact]
    public async Task With_the_worker_running_it_prints_the_id_and_the_note_is_summarized()
    {
        _llm.Reply(Summary);
        var repo = Repo();

        var (exit, json, stdout, _) = Save("We chose X over Y because Z.", new FactoryTransport(_factory, running: true),
            "--folder", repo, "--title", "Test ruling", "--type", "decision", "--source", "chorus-council test", "--json");

        exit.ShouldBe(0);
        stdout.Trim().ShouldNotContain("\n"); // one line
        json.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "project", "spooled", "summarized", "title", "type", "warnings"]);
        var id = json.GetProperty("id").GetInt64();
        id.ShouldBeGreaterThan(0);
        json.GetProperty("project").GetString().ShouldBe("github.com/example/sample-app");
        json.GetProperty("spooled").GetBoolean().ShouldBeFalse();
        json.GetProperty("summarized").GetString().ShouldBe("queued");
        json.GetProperty("title").GetString().ShouldBe("Test ruling");
        json.GetProperty("type").GetString().ShouldBe("decision");

        (await WaitForAsync(() => _factory.Count("SELECT count(*) FROM notes WHERE summarized_at IS NOT NULL") == 1)).ShouldBeTrue();
        using var client = _factory.CreateHookClient();
        var recent = await client.GetFromJsonAsync<JsonElement>(new Uri("/recent", UriKind.Relative), Ct);
        var o = recent.GetProperty("observations")[0];
        o.GetProperty("title").GetString().ShouldBe("Test ruling");
        o.GetProperty("narrative").GetString().ShouldBe("X won because of Z.");
        o.GetProperty("note").GetString().ShouldBe("done");
        o.GetProperty("source").GetString().ShouldBe("chorus-council test");
    }

    [Fact]
    public async Task With_the_worker_stopped_it_spools_and_the_note_is_stored_when_the_worker_reads_the_spool()
    {
        _llm.Reply(Summary);
        var repo = Repo();

        var (exit, json, _, _) = Save("We chose X over Y because Z.", new FactoryTransport(_factory, running: false),
            "--folder", repo, "--title", "Test ruling", "--type", "decision", "--source", "chorus-council test", "--json");

        exit.ShouldBe(0);
        json.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("project").GetString().ShouldBe("github.com/example/sample-app");
        json.GetProperty("spooled").GetBoolean().ShouldBeTrue();
        json.GetProperty("summarized").GetString().ShouldBe("pending");
        json.GetProperty("title").GetString().ShouldBe("Test ruling");
        json.GetProperty("type").GetString().ShouldBe("decision");
        _factory.Count("SELECT count(*) FROM notes").ShouldBe(0);
        var spooled = Directory.GetFiles(EngramPaths.NoteSpool(_factory.Home), "*.json").ShouldHaveSingleItem();
        File.ReadAllText(spooled).ShouldNotContain(_factory.Token);

        // status --json counts it while it waits.
        var status = new StringWriter();
        StatusCommand.RunJson(_env, status);
        JsonDocument.Parse(status.ToString()).RootElement.GetProperty("spooled_notes").GetInt32().ShouldBe(1);

        _factory.Services.GetRequiredService<SpoolIngestor>().IngestNotes().ShouldBe(1);
        (await WaitForAsync(() => _factory.Count("SELECT count(*) FROM notes WHERE summarized_at IS NOT NULL") == 1)).ShouldBeTrue();
        _factory.Query<string>("SELECT p.identity FROM observations o JOIN projects p ON p.id = o.project_id").Single().ShouldBe("github.com/example/sample-app");
    }

    [Fact]
    public void Without_a_transport_and_no_worker_on_the_port_it_spools()
    {
        var (exit, json, _, _) = Save("A ruling.", transport: null, "--folder", Repo(), "--json");

        exit.ShouldBe(0);
        json.GetProperty("spooled").GetBoolean().ShouldBeTrue();
        json.GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null); // derived later, from the summary
        json.GetProperty("type").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Without_json_it_prints_sentences()
    {
        var (exit, _, stdout, _) = Save("A ruling.", new FactoryTransport(_factory, running: true), "--folder", Repo());

        exit.ShouldBe(0);
        stdout.ShouldStartWith("Saved note #");
        stdout.ShouldContain("engram show");
    }

    [Fact]
    public void Non_ascii_text_survives_and_the_json_output_is_pure_ascii()
    {
        var (exit, json, stdout, _) = Save("Décidé : utiliser « SQLite ». Ça marche.", new FactoryTransport(_factory, running: true), "--folder", Repo(), "--json");

        exit.ShouldBe(0);
        stdout.All(c => c < 128).ShouldBeTrue();
        json.GetProperty("title").GetString().ShouldBe("Décidé : utiliser « SQLite »");
        _factory.Query<string>("SELECT text FROM notes").Single().ShouldBe("Décidé : utiliser « SQLite ». Ça marche.");
    }

    [Theory]
    [InlineData("", "the note is empty (pass its text on stdin)")]
    [InlineData("  \r\n ", "the note is empty (pass its text on stdin)")]
    public void Empty_stdin_is_an_error(string stdin, string error)
    {
        var (exit, json, _, stderr) = Save(stdin, new FactoryTransport(_factory, running: true), "--folder", Repo(), "--json");

        exit.ShouldBe(1);
        json.GetProperty("error").GetString().ShouldBe(error);
        stderr.ShouldContain(error);
        _factory.Count("SELECT count(*) FROM observations").ShouldBe(0);
    }

    [Fact]
    public void A_missing_folder_is_an_error()
    {
        var missing = _factory.Temp.Combine("My Projects", "Not There");
        var (exit, json, _, _) = Save("A ruling.", new FactoryTransport(_factory, running: true), "--folder", missing, "--json");

        exit.ShouldBe(1);
        json.GetProperty("error").GetString().ShouldBe($"{missing} is not a folder.");
    }

    [Fact]
    public void A_note_over_the_limit_is_an_error_and_nothing_is_spooled()
    {
        var (exit, json, _, _) = Save(new string('x', SaveCommand.MaxBytes + 1), new FactoryTransport(_factory, running: false), "--folder", Repo(), "--json");

        exit.ShouldBe(1);
        json.GetProperty("error").GetString().ShouldBe("the note is over the 256 KiB limit (262144 bytes of UTF-8)");
        Directory.Exists(EngramPaths.NoteSpool(_factory.Home)).ShouldBeFalse();
    }

    [Fact]
    public void A_note_exactly_at_the_limit_is_accepted()
    {
        var (exit, _, _, _) = Save(new string('x', SaveCommand.MaxBytes), new FactoryTransport(_factory, running: true), "--folder", Repo(), "--json");
        exit.ShouldBe(0);
    }

    [Theory]
    [InlineData("--json", "--folder is required (the project the note belongs to)")]
    [InlineData("--folder|.|--type|opinion|--json", "unknown type 'opinion'; allowed: decision, bugfix, feature, change, discovery, gotcha")]
    [InlineData("--folder|.|--colour|red|--json", "unknown option '--colour' (see engram save --help)")]
    [InlineData("--json|--folder", "--folder needs a value")]
    public void Bad_arguments_are_a_json_error(string args, string error)
    {
        var (exit, json, _, _) = Save("A ruling.", new FactoryTransport(_factory, running: true), args.Split('|'));

        exit.ShouldBe(1);
        json.GetProperty("error").GetString().ShouldBe(error);
    }

    [Fact]
    public void A_terminal_on_stdin_is_an_error_not_a_prompt()
    {
        var stdout = new StringWriter();
        var exit = SaveCommand.Run(["--folder", Repo(), "--json"], _env, Stream.Null, stdinIsTerminal: true, stdout, new StringWriter(), new FactoryTransport(_factory, true), TimeProvider.System);

        exit.ShouldBe(1);
        JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("error").GetString().ShouldBe("pass the note's text on stdin (engram save never prompts)");
    }

    [Fact]
    public void Help_lists_the_flags_and_types()
    {
        var stdout = new StringWriter();
        SaveCommand.Run(["--help"], _env, Stream.Null, stdinIsTerminal: true, stdout, new StringWriter(), null, TimeProvider.System).ShouldBe(0);
        stdout.ToString().ShouldContain("--folder DIR");
        stdout.ToString().ShouldContain("decision, bugfix, feature, change, discovery, gotcha");
    }

    [Fact]
    public void A_worker_that_rejects_the_token_gets_the_note_spooled_with_a_warning()
    {
        var (exit, json, _, stderr) = Save("A ruling.", new RejectingTransport(), "--folder", Repo(), "--json");

        exit.ShouldBe(0);
        json.GetProperty("spooled").GetBoolean().ShouldBeTrue();
        json.GetProperty("warnings")[0].GetString()!.ShouldContain("HTTP 401");
        stderr.ShouldContain("HTTP 401");
    }

    private sealed class RejectingTransport : INoteTransport
    {
        public bool WorkerRunning() => true;

        public (int? Status, string? Body) Send(byte[] json) => (401, string.Empty);
    }
}
