using Engram.Core;
using Engram.Core.Settings;

namespace Engram.Tests.Core;

public sealed class SettingsLoaderTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var (settings, warnings) = SettingsLoader.Load(_temp.Path, Env());

        settings.Worker.ShouldBe(new WorkerSettings());
        settings.Worker.Port.ShouldBe(47613);
        settings.Capture.ExcludePaths.ShouldBe(new CaptureSettings().ExcludePaths);
        settings.Capture.MaxStringBytes.ShouldBe(4096);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_json_with_comments_and_keeps_defaults_for_missing_keys()
    {
        File.WriteAllText(EngramPaths.SettingsFile(_temp.Path), """
            {
              // user settings
              "worker": { "port": 5000 },
              "capture": { "toolMatcher": "Bash", "redactPatterns": ["ACME-\\d+"], },
              "observer": { "provider": "ollama" }
            }
            """);

        var (settings, warnings) = SettingsLoader.Load(_temp.Path, Env());

        warnings.ShouldBeEmpty();
        settings.Worker.Port.ShouldBe(5000);
        settings.Worker.IdleShutdownMinutes.ShouldBe(60);
        settings.Capture.ToolMatcher.ShouldBe("Bash");
        settings.Capture.RedactPatterns.ShouldBe(["ACME-\\d+"]);
        settings.Capture.ExcludePaths.ShouldNotBeEmpty();
    }

    [Fact]
    public void Environment_overrides_the_file()
    {
        File.WriteAllText(EngramPaths.SettingsFile(_temp.Path), """{ "worker": { "port": 5000 } }""");

        var (settings, _) = SettingsLoader.Load(_temp.Path, Env(("ENGRAM_PORT", "6000")));

        settings.Worker.Port.ShouldBe(6000);
    }

    [Fact]
    public void Broken_file_falls_back_to_defaults_with_a_warning()
    {
        File.WriteAllText(EngramPaths.SettingsFile(_temp.Path), "{ not json");

        var (settings, warnings) = SettingsLoader.Load(_temp.Path, Env());

        settings.Worker.ShouldBe(new WorkerSettings());
        settings.Capture.ToolMatcher.ShouldBe(new CaptureSettings().ToolMatcher);
        warnings.ShouldHaveSingleItem().ShouldContain("Using defaults");
    }

    [Fact]
    public void Nulls_and_out_of_range_values_are_normalized()
    {
        File.WriteAllText(EngramPaths.SettingsFile(_temp.Path), """
            { "worker": { "port": 70000 }, "capture": { "maxStringBytes": 10, "excludePaths": null, "toolMatcher": "" } }
            """);

        var (settings, warnings) = SettingsLoader.Load(_temp.Path, Env());

        settings.Worker.Port.ShouldBe(47613);
        settings.Capture.MaxStringBytes.ShouldBe(256);
        settings.Capture.ExcludePaths.ShouldBe(new CaptureSettings().ExcludePaths);
        settings.Capture.ToolMatcher.ShouldBe(new CaptureSettings().ToolMatcher);
        warnings.Count.ShouldBe(2);
    }

    [Fact]
    public void Repo_file_is_read_when_present()
    {
        File.WriteAllText(Path.Combine(_temp.Path, ".engram.json"), """{ "capture": { "enabled": false } }""");

        SettingsLoader.LoadRepo(_temp.Path)?.Capture?.Enabled.ShouldBe(false);
        SettingsLoader.LoadRepo(_temp.Combine("missing")).ShouldBeNull();
    }
}
