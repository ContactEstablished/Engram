using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Engram.Cli.Spike;

namespace Engram.Tests.Spike;

public sealed class SpikeCaptureTests : IDisposable
{
    private const string StopPayload = """
        {"session_id":"s1","transcript_path":"/t.jsonl","cwd":"/repo","hook_event_name":"Stop","stop_hook_active":false}
        """;

    private readonly TempDir _temp = new();
    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();

    private string SpikeDir => _temp.Combine("spike");

    public void Dispose()
    {
        _temp.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
    }

    private int Run(string stdin, string? home = null, bool disabled = false) =>
        Run(new MemoryStream(Encoding.UTF8.GetBytes(stdin)), home, disabled);

    private int Run(Stream stdin, string? home = null, bool disabled = false)
    {
        var env = new Dictionary<string, string?>
        {
            ["ENGRAM_HOME"] = home ?? _temp.Path,
            ["ENGRAM_DISABLE"] = disabled ? "1" : null,
        };
        return SpikeCapture.Run(new CaptureContext(stdin, _stdout, _stderr, k => env.GetValueOrDefault(k), Stopwatch.GetTimestamp(), () => 12.5));
    }

    private void ShouldBeSilent()
    {
        _stdout.Length.ShouldBe(0, "a hook handler must never write to stdout");
        _stderr.Length.ShouldBe(0, "a hook handler must never write to stderr");
    }

    private string ErrorsLog => File.ReadAllText(Path.Combine(SpikeDir, "errors.log"));

    private string[] JsonlFiles => Directory.Exists(SpikeDir) ? Directory.GetFiles(SpikeDir, "*.jsonl") : [];

    [Fact]
    public void Empty_stdin_exits_0_silently_and_logs_the_error()
    {
        Run("").ShouldBe(0);

        ShouldBeSilent();
        JsonlFiles.ShouldBeEmpty();
        ErrorsLog.ShouldContain("empty stdin");
    }

    [Fact]
    public void Whitespace_only_stdin_counts_as_empty()
    {
        Run("  \r\n ").ShouldBe(0);

        ShouldBeSilent();
        ErrorsLog.ShouldContain("empty stdin");
    }

    [Fact]
    public void Malformed_json_exits_0_silently_and_logs_the_input()
    {
        Run("{\"hook_event_name\": \"Stop\", oops").ShouldBe(0);

        ShouldBeSilent();
        JsonlFiles.ShouldBeEmpty();
        ErrorsLog.ShouldContain("oops");
    }

    [Fact]
    public void ENGRAM_DISABLE_exits_immediately_without_reading_stdin_or_touching_disk()
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(StopPayload));

        Run(stdin, disabled: true).ShouldBe(0);

        ShouldBeSilent();
        stdin.Position.ShouldBe(0);
        Directory.Exists(SpikeDir).ShouldBeFalse();
    }

    [Fact]
    public void Unwritable_folder_exits_0_silently()
    {
        // ENGRAM_HOME goes *through* a regular file, so neither the capture file nor errors.log can be created.
        var blocker = _temp.Combine("blocker");
        File.WriteAllText(blocker, "not a directory");
        var sw = Stopwatch.StartNew();

        Run(StopPayload, home: Path.Combine(blocker, "home")).ShouldBe(0);

        ShouldBeSilent();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "non-retryable errors must not spin");
    }

    [Fact]
    public void Valid_payload_appends_one_line_with_metadata_and_the_payload_intact()
    {
        Run(StopPayload).ShouldBe(0);

        ShouldBeSilent();
        var lines = File.ReadAllLines(Path.Combine(SpikeDir, "Stop.jsonl"));
        lines.Length.ShouldBe(1);

        using var record = JsonDocument.Parse(lines[0]);
        var root = record.RootElement;
        root.GetProperty("received_at").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(1));
        root.GetProperty("handler_ms").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        root.GetProperty("process_ms").GetDouble().ShouldBe(12.5);
        root.GetProperty("os").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("stdin_bytes").GetInt32().ShouldBe(Encoding.UTF8.GetByteCount(StopPayload));

        using var original = JsonDocument.Parse(StopPayload);
        JsonElement.DeepEquals(root.GetProperty("payload"), original.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void Pretty_printed_payload_is_stored_on_one_line()
    {
        Run("{\n  \"hook_event_name\": \"Stop\",\n  \"session_id\": \"s\"\n}\n").ShouldBe(0);

        File.ReadAllLines(Path.Combine(SpikeDir, "Stop.jsonl")).Length.ShouldBe(1);
    }

    [Fact]
    public void Non_ascii_text_round_trips_as_utf8()
    {
        const string prompt = "café — 日本語 — emoji 🚀 — <tag> & \"quotes\"";
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["hook_event_name"] = "UserPromptSubmit",
            ["prompt"] = prompt,
        }, PayloadJson.Default.DictionaryStringString);

        Run(payload).ShouldBe(0);

        var line = File.ReadAllText(Path.Combine(SpikeDir, "UserPromptSubmit.jsonl"), Encoding.UTF8);
        line.ShouldContain("café — 日本語"); // stored readable, not \u-escaped
        using var record = JsonDocument.Parse(line);
        record.RootElement.GetProperty("payload").GetProperty("prompt").GetString().ShouldBe(prompt);
    }

    [Theory]
    [InlineData("""{"session_id":"s"}""", "unknown")]
    [InlineData("""{"hook_event_name":42}""", "unknown")]
    [InlineData("""{"hook_event_name":"../../evil"}""", "______evil")]
    [InlineData("""{"hook_event_name":"Notification"}""", "Notification")]
    public void Event_name_maps_to_a_safe_file_name(string payload, string expectedFile)
    {
        Run(payload).ShouldBe(0);

        File.Exists(Path.Combine(SpikeDir, expectedFile + ".jsonl")).ShouldBeTrue();
        JsonlFiles.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_hooks_never_interleave_lines()
    {
        const int writers = 24;
        var env = new Dictionary<string, string?> { ["ENGRAM_HOME"] = _temp.Path };

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            var payload = StopPayload.Replace("\"s1\"", $"\"s{i}\"", StringComparison.Ordinal);
            using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            return SpikeCapture.Run(new CaptureContext(stdin, Stream.Null, Stream.Null, k => env.GetValueOrDefault(k), Stopwatch.GetTimestamp(), () => null));
        }, TestContext.Current.CancellationToken)));

        var lines = File.ReadAllLines(Path.Combine(SpikeDir, "Stop.jsonl"));
        lines.Length.ShouldBe(writers);
        lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("payload").GetProperty("session_id").GetString())
            .Distinct().Count().ShouldBe(writers);
        File.Exists(Path.Combine(SpikeDir, "errors.log")).ShouldBeFalse();
    }

    [Fact]
    public async Task Real_process_prints_nothing_and_exits_0()
    {
        // Runs the built CLI (framework-dependent) exactly as a hook would: stdin pipe, inspect stdout/stderr.
        var engramDll = Path.Combine(AppContext.BaseDirectory, "engram.dll");
        File.Exists(engramDll).ShouldBeTrue();

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { engramDll, "spike", "capture" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ENGRAM_HOME"] = _temp.Path;
        psi.Environment.Remove("ENGRAM_DISABLE");

        using var process = Process.Start(psi).ShouldNotBeNull();
        await process.StandardInput.WriteAsync(StopPayload);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        process.ExitCode.ShouldBe(0);
        stdout.ShouldBeEmpty();
        stderr.ShouldBeEmpty();
        File.ReadAllLines(Path.Combine(SpikeDir, "Stop.jsonl")).Length.ShouldBe(1);
    }
}
