using System.Net;
using System.Text;
using Engram.Core;
using Engram.Core.Settings;
using Engram.Worker.Observer;

namespace Engram.Tests.Observer;

public sealed class ProviderTests : IDisposable
{
    // The shape `claude -p --output-format json` printed on 2.1.330 (trimmed).
    private const string Envelope = """
        {"type":"result","subtype":"success","is_error":false,"duration_ms":4072,"num_turns":1,"result":"{\"observations\":[]}",
         "total_cost_usd":0.0002,"usage":{"input_tokens":2,"cache_creation_input_tokens":697,"cache_read_input_tokens":120,"output_tokens":9},
         "modelUsage":{"claude-haiku-5-5":{"inputTokens":599,"outputTokens":35}},"credential_expires_at":"2026-12-08T09:41:54.816Z"}
        """;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class FakeRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return Task.FromResult(respond(spec));
        }
    }

    private string FakeClaude()
    {
        var path = _temp.Combine(OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private ClaudeCliClient Client(FakeRunner runner, string? claudePath = null) =>
        new(new ObserverSettings { ClaudePath = claudePath ?? FakeClaude() }, _temp.Path, runner, _ => null);

    [Fact]
    public async Task Claude_is_run_minimal_sandboxed_and_guarded_against_recursion()
    {
        var runner = new FakeRunner(_ => new ProcessResult(0, Envelope, string.Empty));

        var response = await Client(runner).CompleteAsync(new LlmRequest("SYSTEM", "USER TEXT"), TestContext.Current.CancellationToken);

        var spec = runner.Specs.ShouldHaveSingleItem();
        spec.StdIn.ShouldBe("USER TEXT", "the turn goes through stdin, never the command line");
        spec.WorkingDirectory.ShouldBe(EngramPaths.ObserverSandbox(_temp.Path));
        Directory.Exists(spec.WorkingDirectory).ShouldBeTrue();
        spec.Environment["ENGRAM_DISABLE"].ShouldBe("1");
        var args = spec.Arguments.ToList();
        args.ShouldContain("-p");
        args.ShouldContain("--strict-mcp-config");
        args.ShouldContain("--no-session-persistence");
        args.ShouldContain("--disable-slash-commands");
        args[args.IndexOf("--tools") + 1].ShouldBe(string.Empty, "no tools");
        args[args.IndexOf("--model") + 1].ShouldBe("haiku");
        args[args.IndexOf("--output-format") + 1].ShouldBe("json");
        args[args.IndexOf("--setting-sources") + 1].ShouldBe("local", "no user settings, hooks or plugins");
        args[args.IndexOf("--system-prompt") + 1].ShouldBe("SYSTEM");
        args.ShouldNotContain("--bare", "--bare would require an API key and reject the subscription login");

        response.Text.ShouldBe("""{"observations":[]}""");
        response.Model.ShouldBe("claude-haiku-5-5");
        response.Usage.ShouldBe(new LlmUsage(2, 9, 120, 697, 0.0002));
    }

    [Theory]
    [InlineData(1, """{"is_error":false,"result":"x"}""", "exit 1")]
    [InlineData(0, """{"is_error":true,"result":"Credit balance is too low"}""", "Credit balance")]
    [InlineData(0, "Segmentation fault", "without JSON")]
    [InlineData(0, """{"is_error":false}""", "failed")]
    public async Task Claude_failures_become_llm_exceptions(int exit, string stdout, string expected)
    {
        var runner = new FakeRunner(_ => new ProcessResult(exit, stdout, "stderr text"));

        var ex = await Should.ThrowAsync<LlmException>(() => Client(runner).CompleteAsync(new LlmRequest("s", "u"), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public async Task A_missing_claude_binary_says_how_to_fix_it()
    {
        var client = new ClaudeCliClient(new ObserverSettings { ClaudePath = _temp.Combine("nope") }, _temp.Path, new FakeRunner(_ => throw new InvalidOperationException()), _ => null);

        var ex = await Should.ThrowAsync<LlmException>(() => client.CompleteAsync(new LlmRequest("s", "u"), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("observer.claudePath");
    }

    [Fact]
    public void Claude_is_found_on_PATH()
    {
        var claude = FakeClaude();

        ClaudeCliClient.Locate(null, name => name == "PATH" ? _temp.Path : null).ShouldBe(claude);
        ClaudeCliClient.Locate(claude, _ => null).ShouldBe(claude);
        ClaudeCliClient.Locate(_temp.Combine("missing"), _ => null).ShouldBeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, LastBody ?? string.Empty);
        }
    }

    [Fact]
    public async Task Ollama_gets_a_json_chat_request_and_reports_token_counts_at_zero_cost()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"message":{"role":"assistant","content":"{\"observations\":[]}"},"prompt_eval_count":321,"eval_count":12}""", Encoding.UTF8, "application/json"),
        });
        var client = new OllamaClient(new ObserverSettings(), new HttpClient(handler));

        var response = await client.CompleteAsync(new LlmRequest("SYS", "USR"), TestContext.Current.CancellationToken);

        handler.LastUri.ShouldBe(new Uri("http://127.0.0.1:11434/api/chat"));
        handler.LastBody.ShouldNotBeNull().ShouldContain("\"format\":\"json\"");
        handler.LastBody.ShouldContain("\"stream\":false");
        handler.LastBody.ShouldContain("\"qwen3:14b\"");
        response.Text.ShouldBe("""{"observations":[]}""");
        response.Usage.ShouldBe(new LlmUsage(321, 12, null, null, 0));
    }

    [Fact]
    public async Task Ollama_errors_become_llm_exceptions()
    {
        var down = new OllamaClient(new ObserverSettings(), new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("connection refused"))));
        var broken = new OllamaClient(new ObserverSettings(), new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("model not found") })));

        (await Should.ThrowAsync<LlmException>(() => down.CompleteAsync(new LlmRequest("s", "u"), TestContext.Current.CancellationToken))).Message.ShouldContain("connection refused");
        (await Should.ThrowAsync<LlmException>(() => broken.CompleteAsync(new LlmRequest("s", "u"), TestContext.Current.CancellationToken))).Message.ShouldContain("404");
    }
}
