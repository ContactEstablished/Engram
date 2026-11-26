using Engram.Worker.Observer;

namespace Engram.Tests.Observer;

public sealed class OutputParserTests
{
    private const string Good = """{"observations":[{"type":"decision","title":"Use webhooks, not polling","narrative":"Chose webhooks. Polling wasted calls.","facts":["port 47613"],"files":["src/Bridge.cs"]}]}""";

    [Fact]
    public void Parses_a_valid_object()
    {
        var (observations, error) = OutputParser.ParseObservations(Good);

        error.ShouldBeNull();
        var o = observations.ShouldNotBeNull().ShouldHaveSingleItem();
        o.Type.ShouldBe("decision");
        o.Title.ShouldBe("Use webhooks, not polling");
        o.Facts.ShouldBe(["port 47613"]);
        o.Files.ShouldBe(["src/Bridge.cs"]);
    }

    [Theory]
    [InlineData("```json\n{\"observations\":[]}\n```")]
    [InlineData("Here you go: {\"observations\":[]} Hope that helps!")]
    [InlineData("[]")]
    [InlineData("{\"observations\":[],}")]
    public void Tolerates_fences_chatter_bare_arrays_and_trailing_commas(string reply)
    {
        var (observations, error) = OutputParser.ParseObservations(reply);

        error.ShouldBeNull();
        observations.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("I could not find anything.", "no JSON")]
    [InlineData("{\"observations\": [", "not valid JSON")]
    [InlineData("{\"things\":[]}", "\"observations\" array")]
    [InlineData("""{"observations":["x"]}""", "not an object")]
    [InlineData("""{"observations":[{"type":"opinion","title":"t","narrative":"n"}]}""", "allowed: decision")]
    [InlineData("""{"observations":[{"type":"gotcha","title":"","narrative":"n"}]}""", "non-empty title")]
    [InlineData("""{"observations":[{"type":"gotcha","title":"t"}]}""", "non-empty title and narrative")]
    public void Reports_structural_problems_for_the_repair_retry(string reply, string expectedInError)
    {
        var (observations, error) = OutputParser.ParseObservations(reply);

        observations.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain(expectedInError);
    }

    [Fact]
    public void Normalizes_type_case_and_clamps_lengths_and_counts()
    {
        var longTitle = new string('t', 300);
        var items = string.Join(",", Enumerable.Range(0, 6).Select(i => $$"""{"type":"BugFix","title":"{{longTitle}}","narrative":"n{{i}}","facts":[{{string.Join(",", Enumerable.Repeat("\"f\"", 20))}}],"files":[]}"""));

        var (observations, error) = OutputParser.ParseObservations($$"""{"observations":[{{items}}]}""");

        error.ShouldBeNull();
        observations.ShouldNotBeNull().Count.ShouldBe(OutputParser.MaxObservations);
        observations[0].Type.ShouldBe("bugfix");
        observations[0].Title.Length.ShouldBe(120);
        observations[0].Facts.Count.ShouldBe(8);
    }

    [Fact]
    public void Parses_a_session_summary()
    {
        var (summary, error) = OutputParser.ParseSummary("""{"request":"fix build","investigated":"","learned":null,"completed":"fixed","next_steps":"none"}""");

        error.ShouldBeNull();
        summary.ShouldBe(new SessionSummary("fix build", "", "", "fixed", "none"));
    }

    [Fact]
    public void A_summary_missing_a_field_is_reported()
    {
        OutputParser.ParseSummary("""{"request":"x"}""").Error.ShouldNotBeNull().ShouldContain("investigated");
    }
}
