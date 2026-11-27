using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Engram.Core;
using Engram.Tests.Observer;
using Engram.Tests.Worker;
using Engram.Worker.Capture;
using Microsoft.Extensions.DependencyInjection;

namespace Engram.Tests.Memory;

/// <summary>Notes from <c>engram save</c>, through the real worker with a fake summarizer.</summary>
public sealed class NoteTests : IDisposable
{
    private const string Summary = """{"observations":[{"type":"decision","title":"Chose SQLite over Postgres for local memory","narrative":"SQLite won because it needs no server.","facts":["one file per user"],"files":[]}]}""";

    private readonly FakeLlmClient _llm = new();
    private readonly WorkerFactory _factory;
    private readonly HttpClient _client;
    private readonly string _folder;

    public NoteTests()
    {
        _factory = new WorkerFactory("""{ "observer": { "provider": "claude-cli" }, "search": { "semantic": false } }""", _llm);
        _client = _factory.CreateHookClient();
        _folder = _factory.Temp.Combine("My Repo");
        Directory.CreateDirectory(_folder);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(object request)
    {
        using var response = await _client.PostAsJsonAsync(new Uri("/memory/note", UriKind.Relative), request, Ct);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private async Task<bool> SummarizedAsync(long id)
    {
        for (var i = 0; i < 300; i++)
        {
            if (_factory.Count("SELECT count(*) FROM notes WHERE observation_id = @id AND summarized_at IS NOT NULL", new { id }) == 1)
            {
                return true;
            }

            await Task.Delay(50, Ct);
        }

        return false;
    }

    private async Task<string> GetAsync(long id) => await _client.GetStringAsync(new Uri($"/memory/get?ids={id}", UriKind.Relative), Ct);

    [Fact]
    public async Task A_note_is_stored_at_once_then_summarized_and_its_full_text_stays_available()
    {
        _llm.Reply(Summary);
        const string text = "We compared SQLite and Postgres for the memory store. Postgres needs a server; SQLite is one file. We chose SQLite.";

        var (status, body) = await PostAsync(new { client_id = "c1", text, folder = _folder, source = "chorus-council run 42" });

        status.ShouldBe(HttpStatusCode.OK);
        var id = body.GetProperty("id").GetInt64();
        body.GetProperty("summarized").GetString().ShouldBe("queued");
        body.GetProperty("title").GetString().ShouldBe("We compared SQLite and Postgres for the memory store"); // provisional: the first sentence
        body.GetProperty("project").GetString()!.ShouldStartWith("dir:");
        body.GetProperty("warnings").GetArrayLength().ShouldBe(0);

        (await SummarizedAsync(id)).ShouldBeTrue("the summarize-note job should run");
        var row = _factory.Query<(string Type, string Title, string Narrative, string Provider, string Model)>(
            "SELECT type, title, narrative, provider, model FROM observations WHERE id = @id", new { id }).Single();
        row.Type.ShouldBe("decision");
        row.Title.ShouldBe("Chose SQLite over Postgres for local memory"); // no title given: the summary's
        row.Narrative.ShouldBe("SQLite won because it needs no server.");
        row.Provider.ShouldBe("note");
        row.Model.ShouldBe("fake/fake-1");
        _llm.Requests.Single().User.ShouldContain(text);
        _llm.Requests.Single().User.ShouldContain("source=\"chorus-council run 42\"");

        var full = await GetAsync(id);
        full.ShouldContain("saved note from chorus-council run 42");
        full.ShouldContain("Chose SQLite over Postgres for local memory");
        full.ShouldContain("Full note:\n" + text);
        full.ShouldNotContain("summary pending");
    }

    [Theory]
    [InlineData("# Arbiter ruling: how the app stores sessions\n\n## Brief\nThe Council was asked.", "Arbiter ruling: how the app stores sessions")]
    [InlineData("\r\n\r\n**Decision:** use Redis. It survives deploys.", "Decision: use Redis")]
    [InlineData("- We chose X over Y because Z. More text.", "We chose X over Y because Z")]
    public void The_provisional_title_is_the_first_sentence_of_the_first_line_without_markdown(string text, string title) =>
        Engram.Worker.Memory.NoteService.ProvisionalTitle(text).ShouldBe(title);

    [Fact]
    public async Task A_given_title_and_type_are_kept_after_summarizing()
    {
        _llm.Reply(Summary);
        var (_, body) = await PostAsync(new { text = "Long ruling text.", folder = _folder, title = "Test ruling", type = "Gotcha" });
        var id = body.GetProperty("id").GetInt64();
        body.GetProperty("title").GetString().ShouldBe("Test ruling");
        body.GetProperty("type").GetString().ShouldBe("gotcha");

        (await SummarizedAsync(id)).ShouldBeTrue();
        var row = _factory.Query<(string Type, string Title, string Narrative)>("SELECT type, title, narrative FROM observations WHERE id = @id", new { id }).Single();
        row.ShouldBe(("gotcha", "Test ruling", "SQLite won because it needs no server."));
        _llm.Requests.Single().User.ShouldContain("title=\"Test ruling\"");
    }

    [Fact]
    public async Task A_long_note_is_stored_whole_sent_whole_and_summarized_short()
    {
        _llm.Reply(Summary);
        var words = string.Join(' ', Enumerable.Range(1, 4000).Select(i => $"word{i}"));
        var (_, body) = await PostAsync(new { text = words, folder = _folder, type = "decision" });
        var id = body.GetProperty("id").GetInt64();

        _factory.Query<string>("SELECT narrative FROM observations WHERE id = @id", new { id }).Single().Length.ShouldBeLessThanOrEqualTo(700); // provisional clip
        (await SummarizedAsync(id)).ShouldBeTrue();
        _factory.Query<string>("SELECT text FROM notes WHERE observation_id = @id", new { id }).Single().ShouldBe(words);
        _llm.Requests.Single().User.ShouldContain("word4000"); // under the note budget: nothing cut
        _factory.Query<string>("SELECT narrative FROM observations WHERE id = @id", new { id }).Single().ShouldBe("SQLite won because it needs no server.");
        (await GetAsync(id)).ShouldContain("word2000 word2001");
    }

    [Fact]
    public async Task Secrets_are_redacted_before_storing_and_before_the_summarizer_sees_them()
    {
        _llm.Reply(Summary);
        var (_, body) = await PostAsync(new { text = "Use key sk-ant-abcdefghijklmnop1234 for the bridge.", folder = _folder });
        var id = body.GetProperty("id").GetInt64();

        (await SummarizedAsync(id)).ShouldBeTrue();
        _factory.Query<string>("SELECT text FROM notes WHERE observation_id = @id", new { id }).Single().ShouldNotContain("sk-ant-abcdefghijklmnop1234");
        _llm.Requests.Single().User.ShouldNotContain("sk-ant-abcdefghijklmnop1234");
        body.GetProperty("title").GetString()!.ShouldNotContain("sk-ant-abcdefghijklmnop1234");
    }

    [Fact]
    public async Task The_same_client_id_is_stored_once()
    {
        var first = await PostAsync(new { client_id = "same", text = "One note.", folder = _folder });
        var second = await PostAsync(new { client_id = "same", text = "One note.", folder = _folder });

        second.Body.GetProperty("id").GetInt64().ShouldBe(first.Body.GetProperty("id").GetInt64());
        _factory.Count("SELECT count(*) FROM notes").ShouldBe(1);
        _factory.Count("SELECT count(*) FROM jobs WHERE kind = 'summarize-note'").ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"folder":"X"}""", "the note is empty")]
    [InlineData("""{"text":"   ","folder":"X"}""", "the note is empty")]
    [InlineData("""{"text":"hi"}""", "the note needs the project folder")]
    [InlineData("""{"text":"hi","folder":"X","type":"opinion"}""", "unknown type 'opinion'; allowed: decision, bugfix, feature, change, discovery, gotcha")]
    [InlineData("""{"text":"hi","folder":"Z:/no/such/folder/anywhere"}""", "Z:/no/such/folder/anywhere is not a folder")]
    [InlineData("""[1]""", "the request is not a JSON object")]
    public async Task Bad_requests_get_a_json_error_and_store_nothing(string json, string error)
    {
        using var content = new StringContent(json.Replace("\"X\"", JsonSerializer.Serialize(_folder), StringComparison.Ordinal), Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri("/memory/note", UriKind.Relative), content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString().ShouldBe(error);
        _factory.Count("SELECT count(*) FROM observations").ShouldBe(0);
    }

    [Fact]
    public async Task A_note_over_the_size_limit_is_refused()
    {
        var (status, body) = await PostAsync(new { text = new string('x', (256 * 1024) + 1), folder = _folder });

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("the note is over the 256 KiB limit (262144 bytes of UTF-8)");
    }

    [Fact]
    public async Task Without_a_token_the_endpoint_refuses()
    {
        using var anonymous = _factory.CreateClient();
        using var response = await anonymous.PostAsJsonAsync(new Uri("/memory/note", UriKind.Relative), new { text = "hi", folder = _folder }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgetting_a_note_deletes_its_full_text_and_leaves_its_job_nothing_to_summarize()
    {
        using var none = new WorkerFactory(); // observer off, so the job waits for us
        using var client = none.CreateHookClient();
        var folder = none.Temp.Combine("repo");
        Directory.CreateDirectory(folder);
        using var saved = await client.PostAsJsonAsync(new Uri("/memory/note", UriKind.Relative), new { text = "Short-lived.", folder }, Ct);
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt64();

        using (var forget = await client.PostAsync(new Uri($"/memory/forget?id={id}", UriKind.Relative), null, Ct))
        {
            forget.EnsureSuccessStatusCode();
        }

        none.Count("SELECT count(*) FROM notes").ShouldBe(0); // ON DELETE CASCADE
        var store = none.Services.GetRequiredService<Engram.Worker.Observer.ObserverStore>();
        var job = store.ClaimNextJob().ShouldNotBeNull();
        job.Kind.ShouldBe("summarize-note");
        store.LoadNote(job).ShouldBeNull(); // the runner completes such a job without a provider call
    }

    [Fact]
    public async Task Spooled_notes_are_stored_when_the_worker_reads_the_spool_and_bad_files_are_set_aside()
    {
        _llm.Reply(Summary);
        var dir = EngramPaths.NoteSpool(_factory.Home);
        Directory.CreateDirectory(dir);
        var note = JsonSerializer.Serialize(new
        {
            client_id = "spooled-1",
            text = "Spooled ruling.",
            folder = _factory.Temp.Combine("gone"), // the folder no longer exists; the CLI resolved the project already
            title = "Test ruling",
            type = "decision",
            source = "chorus-council test",
            saved_at = "2026-11-20T10:00:00.0000000Z",
            project = new { identity = "github.com/example/sample-app", name = "sample-app", branch = "main" },
        });
        File.WriteAllText(Path.Combine(dir, "20261120100000000-spooled-1.json"), note);
        File.WriteAllText(Path.Combine(dir, "20261120100000001-bad.json"), "{ not json");

        var ingestor = _factory.Services.GetRequiredService<SpoolIngestor>();
        ingestor.IngestNotes().ShouldBe(1);
        ingestor.IngestNotes().ShouldBe(0); // the file is gone; nothing stored twice

        Directory.GetFiles(dir, "*.json").ShouldBeEmpty();
        File.Exists(Path.Combine(dir, "20261120100000001-bad.bad")).ShouldBeTrue();
        var row = _factory.Query<(long Id, string Title, string Created, string Identity)>(
            "SELECT o.id, o.title, o.created_at, p.identity FROM observations o JOIN projects p ON p.id = o.project_id").Single();
        row.Title.ShouldBe("Test ruling");
        row.Created.ShouldStartWith("2026-11-20T10:00:00");
        row.Identity.ShouldBe("github.com/example/sample-app");
        (await SummarizedAsync(row.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Recent_marks_notes_and_whether_their_summary_is_pending()
    {
        using var none = new WorkerFactory(); // observer off: the note stays pending
        using var client = none.CreateHookClient();
        var folder = none.Temp.Combine("repo");
        Directory.CreateDirectory(folder);
        using var response = await client.PostAsJsonAsync(new Uri("/memory/note", UriKind.Relative), new { text = "A ruling.", folder, source = "chorus-council run 7" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("warnings")[0].GetString()!.ShouldContain("the summarizer is off");

        var recent = await client.GetFromJsonAsync<JsonElement>(new Uri("/recent", UriKind.Relative), Ct);
        var o = recent.GetProperty("observations")[0];
        o.GetProperty("note").GetString().ShouldBe("pending");
        o.GetProperty("source").GetString().ShouldBe("chorus-council run 7");

        var printed = new StringWriter();
        Engram.Cli.Commands.RecentCommand.Print(recent.GetRawText(), printed);
        printed.ToString().ShouldContain("saved note from chorus-council run 7; summary pending");
    }
}
