using Engram.Cli.Spike;
using Engram.Core.Hooks;

namespace Engram.Tests.Contract;

/// <summary>
/// Every hook fixture (real captures in tests/fixtures/hooks, docs-derived ones in hooks-synthetic) must
/// deserialize into its Engram.Core model with no missing required fields.
/// </summary>
public sealed class HookFixtureContractTests
{
    private static readonly string[] s_fixtureDirs = ["tests/fixtures/hooks", "tests/fixtures/hooks-synthetic"];

    public static TheoryData<string> AllFixtures() => FixturesIn(s_fixtureDirs);

    public static TheoryData<string> RealFixtures() => FixturesIn(["tests/fixtures/hooks"]);

    private static TheoryData<string> FixturesIn(IEnumerable<string> dirs) => [.. FixtureFiles(dirs)];

    private static IEnumerable<string> FixtureFiles(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            var full = Path.Combine(RepoPaths.Root, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(full, "*.json").Order(StringComparer.Ordinal))
            {
                yield return Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/');
            }
        }
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_deserializes_into_its_model_with_all_required_fields(string fixture)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.Root, fixture));

        var eventName = HookPayloadReader.PeekEventName(bytes).ShouldNotBeNull("fixture has no hook_event_name");
        Path.GetFileName(fixture).Split('.')[0].ShouldBe(eventName, "file name must start with the event name");

        var payload = HookPayloadReader.Read(bytes); // throws on a missing required field or a type mismatch

        if (HookEvents.Phase0.Contains(eventName))
        {
            payload.ShouldNotBeOfType<UnknownHookPayload>();
        }

        if (payload.ExtensionData is { Count: > 0 } extra)
        {
            // Not a failure: new or undocumented fields to record in docs/phase0-findings.md.
            TestContext.Current.TestOutputHelper?.WriteLine($"{fixture}: fields not in the model: {string.Join(", ", extra.Keys)}");
        }
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(RealFixtures))]
    public void Real_fixture_contains_no_personal_data(string fixture)
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root, fixture));

        FixtureSanitizer.FindLeaks(text).ShouldBeEmpty();
    }

    [Fact]
    public void Every_phase0_event_has_at_least_one_fixture()
    {
        var events = FixtureFiles(s_fixtureDirs).Select(f => Path.GetFileName(f).Split('.')[0]).ToHashSet(StringComparer.Ordinal);

        HookEvents.Phase0.Where(e => !events.Contains(e)).ShouldBeEmpty();
    }
}
