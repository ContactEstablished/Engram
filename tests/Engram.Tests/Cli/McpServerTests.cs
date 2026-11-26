using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Engram.Cli.Commands;
using Engram.Cli.Mcp;
using Engram.Tests.Worker;

namespace Engram.Tests.Cli;

/// <summary>Forwards tool calls to an in-process worker, so the CLI tools are tested against the real endpoints.</summary>
internal sealed class FactoryMemoryApi(HttpClient client) : IMemoryApi
{
    public List<string> Calls { get; } = [];

    public (int? Status, string? Body) Get(string pathAndQuery)
    {
        Calls.Add("GET " + pathAndQuery);
        using var response = client.GetAsync(new Uri(pathAndQuery, UriKind.Relative)).GetAwaiter().GetResult();
        return ((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    public (int? Status, string? Body) Post(string pathAndQuery, byte[] body)
    {
        Calls.Add("POST " + pathAndQuery);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = client.PostAsync(new Uri(pathAndQuery, UriKind.Relative), content).GetAwaiter().GetResult();
        return ((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }
}

public sealed class McpServerTests : IDisposable
{
    private readonly WorkerFactory _factory = new();
    private readonly HttpClient _client;
    private readonly FactoryMemoryApi _api;
    private readonly string _cwd;
    private readonly McpServer _server;

    public McpServerTests()
    {
        _client = _factory.CreateHookClient();
        _api = new FactoryMemoryApi(_client);
        _cwd = _factory.Temp.Combine("app").Replace('\\', '/');
        _server = new McpServer(EngramTools.Create(_api, () => _cwd), "0.0.1", EngramTools.Instructions);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private JsonElement Send(string json)
    {
        var bytes = _server.Handle(json).ShouldNotBeNull();
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private (string Text, bool IsError) Call(string tool, string argumentsJson)
    {
        var response = Send("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argumentsJson + "}}");
        var result = response.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void Initialize_echoes_the_protocol_version_and_announces_tools()
    {
        var response = Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.330"}}}""");

        response.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        response.GetProperty("id").GetInt32().ShouldBe(1);
        var result = response.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().ShouldBe("2025-06-18");
        result.GetProperty("capabilities").GetProperty("tools").ValueKind.ShouldBe(JsonValueKind.Object);
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("engram");
        result.GetProperty("instructions").GetString().ShouldNotBeNull().ShouldContain("long-term memory");
    }

    [Fact]
    public void Notifications_get_no_reply_and_unknown_methods_an_error()
    {
        _server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").ShouldBeNull();
        Send("""{"jsonrpc":"2.0","id":"a","method":"resources/list"}""").GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        Send("not json").GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
        Send("""{"jsonrpc":"2.0","id":3,"method":"ping"}""").GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Object);
        Send("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nope"}}""").GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    [Fact]
    public void Tools_list_has_the_five_tools_with_valid_schemas()
    {
        var tools = Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""").GetProperty("result").GetProperty("tools");

        tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ShouldBe(["search", "timeline", "get_observations", "save_memory", "forget"]);
        foreach (var tool in tools.EnumerateArray())
        {
            tool.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
            var schema = tool.GetProperty("inputSchema");
            schema.GetProperty("type").GetString().ShouldBe("object");
            schema.GetProperty("required").GetArrayLength().ShouldBe(1);
        }
    }

    [Fact]
    public void Save_search_show_timeline_and_forget_round_trip_through_the_worker()
    {
        var (saved, saveError) = Call("save_memory", """{"text":"Releases are cut from the release/* branches only. Never tag main.","type":"decision","files":["docs/release.md"]}""");
        saveError.ShouldBeFalse(saved);
        saved.ShouldStartWith("Saved as #1 (decision) for this repository (project \"app\")");

        var (found, _) = Call("search", """{"query":"release branches"}""");
        found.ShouldContain("#1 decision");
        found.ShouldContain("Releases are cut from the release/* branches only (release.md)");

        Call("get_observations", """{"ids":[1]}""").Text.ShouldContain("Never tag main.");
        Call("timeline", """{"id":"#1"}""").Text.ShouldContain("> #1 decision");
        Call("search", """{"query":"release","all_repos":true}""").Text.ShouldContain("in all repos");

        Call("forget", """{"id":1}""").Text.ShouldBe("Deleted #1.");
        var (gone, goneError) = Call("forget", """{"id":1}""");
        goneError.ShouldBeTrue();
        gone.ShouldBe("No observation #1.");

        _api.Calls.ShouldContain(c => c.StartsWith("GET /memory/search?q=release%20branches&cwd=", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_arguments_come_back_as_tool_errors_not_crashes()
    {
        Call("search", "{}").ShouldBe(("query is required.", true));
        Call("get_observations", """{"ids":[]}""").IsError.ShouldBeTrue();
        Call("timeline", """{"id":"abc"}""").IsError.ShouldBeTrue();
        Call("search", """{"query":"x","type":"opinion"}""").IsError.ShouldBeTrue();
    }

    [Fact]
    public void Search_and_show_commands_print_the_same_text()
    {
        Call("save_memory", """{"text":"The worker listens on 47613 on Windows and 47614 in WSL."}""");
        var output = new StringWriter();

        SearchCommand.Search(["worker", "port", "--limit", "3"], _api, () => _cwd, output).ShouldBe(0, output.ToString());
        SearchCommand.Show(["#1"], _api, () => _cwd, output).ShouldBe(0);

        output.ToString().ShouldContain("47614 in WSL");
        Should.Throw<ArgumentException>(() => SearchCommand.Search([], _api, () => _cwd, output));
    }

    [Fact]
    public void The_real_binary_speaks_mcp_on_stdout_and_nothing_else()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "engram.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("mcp");
        psi.Environment["ENGRAM_HOME"] = _factory.Temp.Combine("mcp-home");
        using var process = Process.Start(psi)!;
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        process.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000).ShouldBeTrue();

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2, stdout);
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetInt32().ShouldBe(1);
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("result").GetProperty("tools").GetArrayLength().ShouldBe(5);
        process.ExitCode.ShouldBe(0);
        Encoding.UTF8.GetByteCount(stdout).ShouldBeGreaterThan(0);
    }
}
