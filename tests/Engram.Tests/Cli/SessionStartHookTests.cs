using System.Text;
using Engram.Cli.Commands;
using Engram.Cli.Hooks;
using Engram.Core;
using Engram.Core.Security;

namespace Engram.Tests.Cli;

public sealed class SessionStartHookTests : IDisposable
{
    private const string Payload = """{"hook_event_name":"SessionStart","session_id":"s","transcript_path":"t","cwd":"c","source":"startup"}""";
    private readonly TempDir _temp = new();
    private readonly MemoryStream _stdout = new();
    private readonly List<(int Port, string? Token, string Body)> _posts = [];
    private int _starts;

    public void Dispose()
    {
        _stdout.Dispose();
        _temp.Dispose();
    }

    private SessionStartContext Context(string stdin, int? postStatus, string? disable = null, string? startError = null) => new(
        new MemoryStream(Encoding.UTF8.GetBytes(stdin)),
        _stdout,
        name => name switch
        {
            "ENGRAM_HOME" => _temp.Path,
            "ENGRAM_DISABLE" => disable,
            _ => null,
        },
        (port, token, body) =>
        {
            _posts.Add((port, token, Encoding.UTF8.GetString(body)));
            return postStatus;
        },
        _ =>
        {
            _starts++;
            return startError;
        });

    private string[] Spooled() =>
        Directory.Exists(EngramPaths.Spool(_temp.Path)) ? Directory.GetFiles(EngramPaths.Spool(_temp.Path)) : [];

    [Fact]
    public void Running_worker_gets_the_payload_with_the_token()
    {
        var token = WorkerToken.ReadOrCreate(_temp.Path);

        SessionStartHook.Run(Context(Payload, 200)).ShouldBe(0);

        _posts.ShouldHaveSingleItem().ShouldBe((47613, token, Payload));
        _starts.ShouldBe(0);
        Spooled().ShouldBeEmpty();
        _stdout.Length.ShouldBe(0, "Phase 1 injects no context");
    }

    [Fact]
    public void Worker_down_spools_the_payload_and_starts_the_worker()
    {
        WorkerToken.ReadOrCreate(_temp.Path);

        SessionStartHook.Run(Context(Payload, null)).ShouldBe(0);

        _starts.ShouldBe(1);
        var spooled = Spooled().ShouldHaveSingleItem();
        spooled.ShouldEndWith(".json");
        File.ReadAllText(spooled).ShouldBe(Payload);
        _stdout.Length.ShouldBe(0);
    }

    [Fact]
    public void No_token_yet_still_spools_and_starts_the_worker()
    {
        SessionStartHook.Run(Context(Payload, 200)).ShouldBe(0);

        _posts.ShouldBeEmpty();
        _starts.ShouldBe(1);
        Spooled().Length.ShouldBe(1);
    }

    [Fact]
    public void Start_failure_is_logged_not_printed()
    {
        SessionStartHook.Run(Context(Payload, null, startError: "boom")).ShouldBe(0);

        _stdout.Length.ShouldBe(0);
        File.ReadAllText(Path.Combine(EngramPaths.Logs(_temp.Path), "hook-errors.log")).ShouldContain("boom");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void Empty_stdin_does_nothing(string stdin)
    {
        SessionStartHook.Run(Context(stdin, null)).ShouldBe(0);

        _posts.ShouldBeEmpty();
        _starts.ShouldBe(0);
        _stdout.Length.ShouldBe(0);
    }

    [Fact]
    public void Disabled_exits_before_reading_stdin()
    {
        var stdin = new ThrowingStream();
        var ctx = Context(Payload, 200, disable: "1") with { Stdin = stdin };

        SessionStartHook.Run(ctx).ShouldBe(0);

        _posts.ShouldBeEmpty();
        Directory.GetFileSystemEntries(_temp.Path).ShouldBeEmpty();
    }

    [Fact]
    public void Install_creates_settings_and_token_and_is_idempotent()
    {
        var output = new StringWriter();
        string? setToken = null;
        Func<string, string?> env = name => name == "ENGRAM_HOME" ? _temp.Path : null;
        var profile = _temp.Combine("profile");

        InstallCommand.Run(new InstallCommand.Options(Yes: true, RotateToken: false, SkipEnvironment: false), env, output, TextReader.Null, t => setToken = t, profile).ShouldBe(0);
        var token = WorkerToken.Read(_temp.Path);
        InstallCommand.Run(new InstallCommand.Options(Yes: true, RotateToken: false, SkipEnvironment: false), env, output, TextReader.Null, t => setToken = t, profile).ShouldBe(0);

        token.ShouldNotBeNull();
        WorkerToken.Read(_temp.Path).ShouldBe(token);
        File.ReadAllText(EngramPaths.SettingsFile(_temp.Path)).ShouldContain("\"toolMatcher\"");
        if (OperatingSystem.IsWindows())
        {
            setToken.ShouldBe(token);
        }
        else
        {
            File.ReadAllText(profile).Split(InstallCommand.ProfileMarker).Length.ShouldBe(2, "added once");
        }

        output.ToString().ShouldContain("kept");
    }

    [Fact]
    public void Install_asks_before_changing_the_environment()
    {
        var output = new StringWriter();
        var called = false;
        Func<string, string?> env = name => name == "ENGRAM_HOME" ? _temp.Path : null;
        var profile = _temp.Combine("profile");

        InstallCommand.Run(new InstallCommand.Options(Yes: false, RotateToken: false, SkipEnvironment: false), env, output, new StringReader("n\n"), _ => called = true, profile);

        called.ShouldBeFalse();
        File.Exists(profile).ShouldBeFalse();
        output.ToString().ShouldContain("[y/N]");
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("stdin must not be read");

        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("stdin must not be read");
    }
}
