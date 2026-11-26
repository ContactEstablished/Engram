using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Engram.Core.Settings;
using Engram.Tests.Worker;
using Engram.Worker.Capture;
using Engram.Worker.Embedding;
using Microsoft.Extensions.DependencyInjection;

namespace Engram.Tests.Memory;

/// <summary>
/// A stand-in for an embedding model: words in the same concept group share a dimension, so "push" and
/// "webhook" are close while sharing no letters. Can be switched off to simulate Ollama being down.
/// </summary>
internal sealed class ConceptEmbedder(string model = "fake-embed") : IEmbeddingClient
{
    private static readonly string[][] Concepts =
    [
        ["webhook", "webhooks", "push", "callback", "notifications", "notification", "delivered", "deliveries"],
        ["poll", "polling", "pull", "interval", "fetch"],
        ["release", "releases", "tag", "tagged", "version", "ship", "shipping"],
        ["bread", "bake", "baking", "oven", "flour"],
        ["database", "sqlite", "migration", "migrations", "schema", "table"],
        ["native", "rebuild", "electron", "module", "node-pty"],
    ];

    private const int Dims = 16;

    public string Model { get; } = model;

    public bool Down { get; set; }

    public int Calls { get; private set; }

    public List<string> Texts { get; } = [];

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls++;
        if (Down)
        {
            throw new EmbeddingException("ollama call failed: connection refused");
        }

        Texts.AddRange(texts);
        return Task.FromResult(texts.Select(Embed).ToArray());
    }

    private static float[] Embed(string text)
    {
        var v = new float[Dims];
        foreach (var word in text.ToLowerInvariant().Split([' ', '.', ',', '?', ':', ';', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var concept = Array.FindIndex(Concepts, c => c.Contains(word));
            if (concept >= 0)
            {
                v[concept] += 1f;
            }
            else
            {
                v[6 + (Math.Abs(word.GetHashCode(StringComparison.Ordinal)) % (Dims - 6))] += 0.15f; // a little noise
            }
        }

        return VectorMath.Normalize(v);
    }
}

public sealed class SemanticSearchTests : IDisposable
{
    private const string Settings = """{ "observer": { "provider": "none" }, "search": { "semantic": true, "minSimilarity": 0.5 } }""";
    private readonly ConceptEmbedder _embedder = new();
    private readonly WorkerFactory _factory;
    private readonly HttpClient _client;
    private readonly string _cwd;
    private readonly long _project;

    public SemanticSearchTests()
    {
        _factory = new WorkerFactory(Settings, fakeEmbedder: _embedder);
        _client = _factory.CreateHookClient();
        _cwd = _factory.Temp.Combine("chorus").Replace('\\', '/');
        _factory.Execute("INSERT INTO projects (identity, name, created_at) VALUES (@identity, 'chorus', '2026-01-01')", new { identity = "dir:" + ProjectResolver.NormalizePath(_cwd) });
        _project = _factory.Count("SELECT id FROM projects");
        _factory.Execute("INSERT INTO sessions (id, project_id, platform, cwd, started_at, last_seen_at) VALUES ('s1', @p, 'windows', @cwd, '2026-01-01', '2026-01-01')", new { p = _project, cwd = _cwd });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long Observe(string type, string title, string narrative)
    {
        _factory.Execute(
            """
            INSERT INTO observations (session_id, project_id, type, title, narrative, facts, files, provider, model, created_at)
            VALUES ('s1', @p, @type, @title, @narrative, '[]', '["src/Bridge.cs"]', 'fake', 'm', @created)
            """,
            new { p = _project, type, title, narrative, created = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        return _factory.Count("SELECT max(id) FROM observations");
    }

    private async Task EmbedAllAsync()
    {
        var expected = _factory.Count("SELECT count(*) FROM observations");
        _factory.Services.GetRequiredService<EmbeddingSignal>().Notify();
        for (var i = 0; i < 200 && _factory.Count("SELECT count(*) FROM observation_vectors") < expected; i++)
        {
            await Task.Delay(25, Ct);
        }

        _factory.Count("SELECT count(*) FROM observation_vectors").ShouldBe(expected, "every observation should be embedded");
    }

    private async Task<string> SearchAsync(string q, string mode = "hybrid") =>
        await _client.GetStringAsync(new Uri($"/memory/search?q={Uri.EscapeDataString(q)}&cwd={Uri.EscapeDataString(_cwd)}&mode={mode}", UriKind.Relative), Ct);

    [Fact]
    public async Task Meaning_finds_what_keywords_miss()
    {
        var webhook = Observe("decision", "Telegram uses webhooks, not polling", "Updates are delivered by webhook callbacks.");
        Observe("gotcha", "node-pty needs a rebuild after Electron upgrades", "The native module breaks.");
        await EmbedAllAsync();

        var keywordOnly = await SearchAsync("how do push notifications reach us", "keyword");
        var hybrid = await SearchAsync("how do push notifications reach us");

        keywordOnly.ShouldStartWith("No observations");
        hybrid.ShouldContain($"#{webhook} decision");
        hybrid.ShouldContain("best first by keyword and meaning");
        hybrid.ShouldNotContain("node-pty", Case.Sensitive, "unrelated memories stay below the similarity floor");
    }

    [Fact]
    public async Task Keyword_and_meaning_hits_are_fused_with_both_lists_counting()
    {
        var both = Observe("discovery", "Shipping happens on Fridays", "We ship every Friday.");                // "friday" + shipping
        var meaningOnlyHit = Observe("decision", "Versions are tagged from branches", "Each version gets a tag."); // shipping, no query word
        var keywordOnlyHit = Observe("change", "Friday standup moved to 10am", "The team meeting is later.");      // "friday", unrelated meaning
        await EmbedAllAsync();

        (await SearchAsync("friday release", "keyword")).ShouldNotContain($"#{meaningOnlyHit} ");
        (await SearchAsync("friday release", "semantic")).ShouldNotContain($"#{keywordOnlyHit} ");
        var text = await SearchAsync("friday release");
        var order = text.Split('\n').Where(l => l.StartsWith('#')).Select(l => long.Parse(l[1..l.IndexOf(' ')], CultureInfo.InvariantCulture)).ToList();

        order[0].ShouldBe(both, "found by both lists, so it ranks first");
        order.ShouldContain(keywordOnlyHit);
        order.ShouldContain(meaningOnlyHit);
    }

    [Fact]
    public async Task When_the_embedding_service_is_down_search_falls_back_to_keywords_and_says_so()
    {
        var webhook = Observe("decision", "Telegram uses webhooks, not polling", "Updates arrive by webhook.");
        await EmbedAllAsync();
        _embedder.Down = true;

        var text = await SearchAsync("webhook");

        text.ShouldContain($"#{webhook}");
        text.ShouldContain("best first by keyword (keyword only: ollama call failed: connection refused)");
    }

    [Fact]
    public async Task New_and_saved_memories_get_embedded_and_forgotten_ones_leave_the_index()
    {
        using var save = await _client.PostAsJsonAsync(new Uri("/memory/save", UriKind.Relative), new { text = "Deploys go out through the release pipeline every Friday.", cwd = _cwd }, Ct);
        var saved = await save.Content.ReadAsStringAsync(Ct);
        var id = long.Parse(saved[10..saved.IndexOf(' ', 10)], CultureInfo.InvariantCulture);
        await EmbedAllAsync();

        (await SearchAsync("shipping schedule", "semantic")).ShouldContain($"#{id}");
        _embedder.Texts.ShouldContain(t => t.StartsWith("search_document: Deploys go out", StringComparison.Ordinal), "documents carry nomic's task prefix");
        _embedder.Texts.ShouldContain("search_query: shipping schedule");

        using var forget = await _client.PostAsync(new Uri($"/memory/forget?id={id}", UriKind.Relative), null, Ct);
        forget.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Count("SELECT count(*) FROM observation_vectors").ShouldBe(0, "ON DELETE CASCADE");
        (await SearchAsync("shipping schedule", "semantic")).ShouldStartWith("No observations");
    }

    [Fact]
    public async Task Stats_report_vector_coverage()
    {
        Observe("decision", "Telegram uses webhooks", "By webhook.");
        await EmbedAllAsync();

        var stats = await _client.GetFromJsonAsync<JsonElement>(new Uri("/stats", UriKind.Relative), Ct);
        var search = stats.GetProperty("search");
        search.GetProperty("semantic").GetBoolean().ShouldBeTrue();
        search.GetProperty("vectors").GetInt32().ShouldBe(1);
        search.GetProperty("model").GetString().ShouldBe("nomic-embed-text");
    }

    [Fact]
    public void Document_text_is_title_narrative_facts_and_files()
    {
        EmbeddingService.DocumentText("Use webhooks.", "Polling wasted calls.", """["port 8443"]""", """["src/Bridge.cs"]""")
            .ShouldBe("Use webhooks. Polling wasted calls. port 8443. Files: src/Bridge.cs");
    }

    [Fact]
    public void Vector_math_is_normalized_cosine()
    {
        var a = VectorMath.Normalize([3f, 4f]);
        a.ShouldBe([0.6f, 0.8f], tolerance: 0.0001f);
        VectorMath.Dot(a, a).ShouldBe(1f, 0.0001f);
        var big = VectorMath.Normalize([.. Enumerable.Range(1, 768).Select(i => (float)i)]);
        VectorMath.Dot(big, big).ShouldBe(1f, 0.001f, "SIMD path and the scalar tail agree");
        VectorMath.FromBytes(VectorMath.ToBytes(big)).ShouldBe(big);
    }

    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(Body ?? string.Empty);
        }
    }

    [Fact]
    public async Task The_ollama_client_batches_keeps_the_model_loaded_and_normalizes()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"model":"nomic-embed-text","embeddings":[[3,4],[0,2]]}""", Encoding.UTF8, "application/json") });
        var client = new OllamaEmbeddingClient(new SearchSettings(), new HttpClient(handler));

        var vectors = await client.EmbedAsync(["a", "b"], TimeSpan.FromSeconds(5), Ct);

        vectors[0].ShouldBe([0.6f, 0.8f], tolerance: 0.0001f);
        vectors[1].ShouldBe([0f, 1f], tolerance: 0.0001f);
        handler.Body.ShouldNotBeNull().ShouldContain("\"keep_alive\":\"30m\"");
        handler.Body.ShouldContain("\"input\":[\"a\",\"b\"]");
        handler.Body.ShouldContain("\"model\":\"nomic-embed-text\"");
    }

    [Fact]
    public async Task The_ollama_client_explains_a_missing_model()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"embeddings":[]}""") });
        var client = new OllamaEmbeddingClient(new SearchSettings(), new HttpClient(handler));

        var ex = await Should.ThrowAsync<EmbeddingException>(() => client.EmbedAsync(["a"], TimeSpan.FromSeconds(5), Ct));

        ex.Message.ShouldContain("ollama pull nomic-embed-text");
    }
}
