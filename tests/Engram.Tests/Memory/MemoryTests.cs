using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Engram.Core.Settings;
using Engram.Tests.Worker;
using Engram.Worker.Capture;
using Engram.Worker.Memory;

namespace Engram.Tests.Memory;

public sealed class MemoryTests : IDisposable
{
    private readonly WorkerFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _cwd;
    private readonly long _project;

    public MemoryTests()
    {
        _client = _factory.CreateHookClient();
        _cwd = _factory.Temp.Combine("chorus").Replace('\\', '/');
        _project = Project(_cwd, "chorus");
        _factory.Execute("INSERT INTO sessions (id, project_id, platform, cwd, started_at, last_seen_at) VALUES ('s1', @p, 'windows', @cwd, '2026-01-01', '2026-01-01')", new { p = _project, cwd = _cwd });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long Project(string cwd, string name)
    {
        _factory.Execute("INSERT INTO projects (identity, name, created_at) VALUES (@identity, @name, '2026-01-01')", new { identity = "dir:" + ProjectResolver.NormalizePath(cwd), name });
        return _factory.Count("SELECT id FROM projects WHERE name = @name", new { name });
    }

    private long Observe(string type, string title, string narrative = "narrative", int daysAgo = 0, string? branch = null, long? project = null, string session = "s1", string files = "[]")
    {
        var created = DateTimeOffset.UtcNow.AddDays(-daysAgo).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        _factory.Execute(
            """
            INSERT INTO observations (session_id, project_id, type, title, narrative, facts, files, branch, provider, model, created_at)
            VALUES (@session, @project, @type, @title, @narrative, '["a fact"]', @files, @branch, 'fake', 'm', @created)
            """,
            new { session, project = project ?? _project, type, title, narrative, files, branch, created });
        return _factory.Count("SELECT max(id) FROM observations");
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> ContextAsync(string source, string session = "new-session")
    {
        var payload = $$"""{"hook_event_name":"SessionStart","session_id":"{{session}}","transcript_path":"t","cwd":"{{_cwd}}","source":"{{source}}"}""";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri("/context", UriKind.Relative), content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private string Q(string s) => Uri.EscapeDataString(s);

    // ---- injection ----------------------------------------------------------------------------------

    [Fact]
    public async Task Context_lists_the_summary_and_ranked_rows_newest_first_and_registers_the_session()
    {
        var old = Observe("change", "Renamed a variable", daysAgo: 60);
        var decision = Observe("decision", "Telegram uses webhooks, not polling", daysAgo: 3, files: """["src/Bridge/TelegramBridge.cs"]""");
        var gotcha = Observe("gotcha", "node-pty needs a rebuild after Electron upgrades", daysAgo: 1);
        _factory.Execute(
            """
            INSERT INTO summaries (session_id, project_id, kind, request, investigated, learned, completed, next_steps, provider, model, created_at)
            VALUES ('s1', @p, 'final', 'Add the Telegram webhook', '', '', 'webhook added', 'retry handling', 'fake', 'm', '2026-11-24T10:00:00Z')
            """,
            new { p = _project });

        var text = await ContextAsync("startup");
        await _factory.DrainAsync();

        var lines = text.TrimEnd('\n').Split('\n');
        lines[0].ShouldBe("Engram memory for this repository (project \"chorus\"): 3 of 3 observations, most relevant first by type and recency. Each line is the title of a longer record.");
        lines[1].ShouldStartWith("Last session 2026-11-24: Add the Telegram webhook; done: webhook added; open: retry handling");
        lines[2].ShouldStartWith($"#{gotcha} gotcha");
        lines[3].ShouldStartWith($"#{decision} decision");
        lines[3].ShouldEndWith("Telegram uses webhooks, not polling (TelegramBridge.cs)");
        lines[4].ShouldStartWith($"#{old} change");
        lines[^1].ShouldContain("engram MCP tools");
        text.ShouldNotContain("narrative", Case.Sensitive, "only index rows are injected");
        _factory.Count("SELECT count(*) FROM sessions WHERE id = 'new-session'").ShouldBe(1, "context also registers the session");
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task Resumed_and_forked_sessions_get_no_injection(string source)
    {
        Observe("decision", "Something worth knowing");

        (await ContextAsync(source)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_repo_without_memory_gets_nothing()
    {
        (await ContextAsync("startup")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Context_stays_under_the_character_budget()
    {
        for (var i = 0; i < 60; i++)
        {
            Observe("decision", $"Decision number {i} " + new string('x', 150));
        }

        var text = await ContextAsync("clear");

        text.Length.ShouldBeLessThanOrEqualTo(new InjectionSettings().MaxChars);
        text.ShouldContain("of 60 observations");
        text.Split('\n').Count(l => l.StartsWith('#')).ShouldBeLessThanOrEqualTo(25);
    }

    [Fact]
    public void Ranking_weighs_type_recency_and_branch()
    {
        var settings = new InjectionSettings();
        var now = DateTimeOffset.UtcNow;
        ObservationRow Row(string type, int daysAgo, string? branch) =>
            new(1, type, "t", "n", "[]", "[]", branch, now.AddDays(-daysAgo).ToString("O", CultureInfo.InvariantCulture), "s", null, "p", "fake");

        ContextBuilder.Score(Row("decision", 0, null), null, now, settings).ShouldBe(3, 0.001);
        ContextBuilder.Score(Row("decision", 21, null), null, now, settings).ShouldBe(1.5, 0.001, "half-life 21 days");
        ContextBuilder.Score(Row("change", 0, "main"), "main", now, settings).ShouldBe(1.5, 0.001, "same-branch boost");
        ContextBuilder.Score(Row("unknown", 0, null), null, now, settings).ShouldBe(1, 0.001);
    }

    // ---- MCP-backing endpoints -------------------------------------------------------------------------

    [Fact]
    public async Task Search_ranks_by_relevance_with_prefixes_stemming_and_an_any_word_fallback()
    {
        var webhook = Observe("decision", "Telegram uses webhooks, not polling", "Updates arrive by webhook.");
        Observe("gotcha", "node-pty needs a rebuild", "Electron upgrades break native modules.");

        (await GetAsync($"/memory/search?q={Q("webhook")}&cwd={Q(_cwd)}")).Body.ShouldContain($"#{webhook} decision");
        (await GetAsync($"/memory/search?q={Q("poll")}&cwd={Q(_cwd)}")).Body.ShouldContain($"#{webhook}");
        var mixed = (await GetAsync($"/memory/search?q={Q("why did we choose webhooks over polling for telegram?")}&cwd={Q(_cwd)}")).Body;
        mixed.ShouldContain($"#{webhook}");
        mixed.ShouldStartWith("1 result(s) in this repository (project \"chorus\")");
        (await GetAsync($"/memory/search?q={Q("kubernetes")}&cwd={Q(_cwd)}")).Body.ShouldBe("No observations in this repository (project \"chorus\") match \"kubernetes\".");
    }

    [Fact]
    public async Task Search_filters_by_type_date_branch_and_scope()
    {
        Observe("decision", "Alpha decision", branch: "main");
        Observe("bugfix", "Alpha bugfix", daysAgo: 30, branch: "feature");
        var otherCwd = _factory.Temp.Combine("other").Replace('\\', '/');
        var other = Project(otherCwd, "other");
        Observe("decision", "Alpha elsewhere", project: other);

        (await GetAsync($"/memory/search?q=alpha&cwd={Q(_cwd)}")).Body.ShouldStartWith("2 result(s)");
        (await GetAsync($"/memory/search?q=alpha&type=bugfix&cwd={Q(_cwd)}")).Body.ShouldContain("Alpha bugfix");
        (await GetAsync($"/memory/search?q=alpha&since=7d&cwd={Q(_cwd)}")).Body.ShouldNotContain("Alpha bugfix");
        (await GetAsync($"/memory/search?q=alpha&branch=feature&cwd={Q(_cwd)}")).Body.ShouldStartWith("1 result(s)");
        var all = (await GetAsync("/memory/search?q=alpha&all=1")).Body;
        all.ShouldStartWith("3 result(s) in all repos");
        all.ShouldContain("other  Alpha elsewhere");
        (await GetAsync($"/memory/search?q=alpha&type=opinion&cwd={Q(_cwd)}")).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"/memory/search?q=alpha&cwd={Q(_factory.Temp.Combine("unknown"))}")).Body.ShouldContain("No memory recorded");
    }

    [Fact]
    public async Task Timeline_and_get_return_neighbours_and_full_records()
    {
        var ids = Enumerable.Range(1, 7).Select(i => Observe("change", $"Step {i}")).ToList();

        var timeline = (await GetAsync($"/memory/timeline?id={ids[3]}&before=2&after=1")).Body;
        timeline.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim()[..2]).ShouldBe(["#" + ids[1].ToString(CultureInfo.InvariantCulture)[0], "#" + ids[2].ToString(CultureInfo.InvariantCulture)[0], "> ", "#" + ids[4].ToString(CultureInfo.InvariantCulture)[0]]);
        timeline.ShouldContain($"> #{ids[3]} change");

        var full = (await GetAsync($"/memory/get?ids={ids[0]},{ids[1]},999")).Body;
        full.ShouldContain($"#{ids[0]} change |");
        full.ShouldContain("Step 1\nnarrative\n- a fact");
        full.ShouldContain("Not found: #999");
        (await GetAsync("/memory/timeline?id=999")).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Saved_memories_are_redacted_searchable_and_can_be_forgotten()
    {
        using var save = await _client.PostAsJsonAsync(
            new Uri("/memory/save", UriKind.Relative),
            new { text = "Deploys go through the staging slot first. The key is sk-ant-api03-abcdefghijklmnop.", type = "decision", cwd = _cwd },
            Ct);
        var saved = await save.Content.ReadAsStringAsync(Ct);
        saved.ShouldStartWith("Saved as #");
        var id = long.Parse(saved[10..saved.IndexOf(' ', 10)], CultureInfo.InvariantCulture);

        var found = (await GetAsync($"/memory/search?q=staging&cwd={Q(_cwd)}")).Body;
        found.ShouldContain($"#{id} decision");
        found.ShouldContain("Deploys go through the staging slot first");
        (await GetAsync($"/memory/get?ids={id}")).Body.ShouldContain("<REDACTED>");
        (await GetAsync($"/memory/get?ids={id}")).Body.ShouldContain("saved on request");

        using var forget = await _client.PostAsync(new Uri($"/memory/forget?id={id}", UriKind.Relative), null, Ct);
        (await forget.Content.ReadAsStringAsync(Ct)).ShouldBe($"Deleted #{id}.");
        (await GetAsync($"/memory/search?q=staging&cwd={Q(_cwd)}")).Body.ShouldContain("No observations");
        _factory.Count("SELECT count(*) FROM observations_fts WHERE observations_fts MATCH 'staging'").ShouldBe(0, "the FTS entry is gone too");
    }

    [Fact]
    public async Task Memory_endpoints_require_the_token()
    {
        using var anonymous = _factory.CreateClient();

        foreach (var path in new[] { "/memory/search?q=x&all=1", "/memory/timeline?id=1", "/memory/get?ids=1" })
        {
            (await anonymous.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await anonymous.PostAsync(new Uri("/context", UriKind.Relative), new StringContent("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(new Uri("/memory/forget?id=1", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_over_10k_observations_is_fast()
    {
        var words = new[] { "webhook", "telegram", "electron", "sqlite", "migration", "worker", "token", "branch", "fixture", "parser" };
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Engram.Core.EngramPaths.Database(_factory.Home)};Pooling=false"))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            for (var i = 0; i < 10_000; i++)
            {
                Dapper.SqlMapper.Execute(
                    c,
                    "INSERT INTO observations (session_id, project_id, type, title, narrative, facts, files, provider, model, created_at) VALUES ('s1', @p, 'change', @title, @narrative, '[]', '[]', 'fake', 'm', '2026-11-01T00:00:00Z')",
                    new { p = _project, title = $"{words[i % 10]} {words[(i * 7) % 10]} change {i}", narrative = $"Narrative about {words[(i * 3) % 10]} and item {i}." },
                    tx);
            }

            tx.Commit();
        }
        await GetAsync($"/memory/search?q=warmup&cwd={Q(_cwd)}");

        var timings = new List<double>();
        foreach (var q in new[] { "webhook", "telegram electron", "sqlite migration worker", "why does the parser break fixtures" })
        {
            var sw = Stopwatch.StartNew();
            var (status, body) = await GetAsync($"/memory/search?q={Q(q)}&cwd={Q(_cwd)}");
            timings.Add(sw.Elapsed.TotalMilliseconds);
            status.ShouldBe(HttpStatusCode.OK);
            body.ShouldContain("result(s)");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"search over 10k: {string.Join(", ", timings.Select(t => t.ToString("0.0", CultureInfo.InvariantCulture)))} ms");
        timings.Max().ShouldBeLessThan(500, "plan budget is 100 ms; generous here to avoid CI noise");
    }
}

public sealed class DeriveTitleTests
{
    [Theory]
    [InlineData("Deploys go through staging first. Then prod.", "Deploys go through staging first")]
    [InlineData("The changelog generator fails silently when a commit subject starts with a colon; such commits are dropped from CHANGELOG.md.", "The changelog generator fails silently when a commit subject starts with a colon")]
    [InlineData("Version numbers come from the release/* branch name, not the csproj.", "Version numbers come from the release/* branch name, not the csproj")]
    [InlineData("Uses v1.2.3 tags", "Uses v1.2.3 tags")]
    public void Titles_are_the_first_sentence_whole(string text, string expected) =>
        Engram.Worker.Memory.MemoryEndpoints.DeriveTitle(text).ShouldBe(expected);

    [Fact]
    public void Very_long_sentences_are_cut_at_a_word()
    {
        var title = Engram.Worker.Memory.MemoryEndpoints.DeriveTitle(string.Join(' ', Enumerable.Repeat("word", 60)));

        title.Length.ShouldBeLessThanOrEqualTo(120);
        title.ShouldEndWith("word…");
    }
}

public sealed class KeywordQueryTests : IDisposable
{
    private readonly WorkerFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _cwd;

    public KeywordQueryTests()
    {
        _client = _factory.CreateHookClient();
        _cwd = _factory.Temp.Combine("chorus").Replace('\\', '/');
        _factory.Execute("INSERT INTO projects (identity, name, created_at) VALUES (@identity, 'chorus', '2026-01-01')", new { identity = "dir:" + ProjectResolver.NormalizePath(_cwd) });
        _factory.Execute("INSERT INTO sessions (id, project_id, platform, cwd, started_at, last_seen_at) VALUES ('s1', 1, 'windows', @cwd, '2026-01-01', '2026-01-01')", new { cwd = _cwd });
        _factory.Execute("INSERT INTO observations (session_id, project_id, type, title, narrative, provider, model, created_at) VALUES ('s1', 1, 'decision', 'Telegram uses webhooks', 'Updates arrive by webhook.', 'f', 'm', '2026-11-01')");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private Task<string> SearchAsync(string q) =>
        _client.GetStringAsync(new Uri($"/memory/search?q={Uri.EscapeDataString(q)}&cwd={Uri.EscapeDataString(_cwd)}", UriKind.Relative), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("how do we get it to us")]          // only stop words: no keyword match at all
    [InlineData("how do push notifications reach us")] // "us" must not prefix-match "uses"
    public async Task Question_words_do_not_match_everything(string question) =>
        (await SearchAsync(question)).ShouldStartWith("No observations");

    [Fact]
    public async Task Real_words_in_a_question_still_match() =>
        (await SearchAsync("why do we use webhooks?")).ShouldContain("#1 decision");
}
