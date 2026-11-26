using Engram.Core.Security;

namespace Engram.Tests.Core;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("key sk-ant-api03-abcdefghijklmnop", "key <REDACTED>")]
    [InlineData("export OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwx", "export OPENAI_API_KEY=<REDACTED>")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123", "<REDACTED>")]
    [InlineData("AKIAABCDEFGHIJKLMNOP", "<REDACTED>")]
    [InlineData("Authorization: Bearer abcdefghijklmnop.qrs", "Authorization: Bearer <REDACTED>")]
    [InlineData("Server=x;User Id=sa;Password=hunter22;", "Server=x;User Id=sa;Password=<REDACTED>;")]
    [InlineData("AccountKey=abc123def456==;EndpointSuffix=core.windows.net", "AccountKey=<REDACTED>;EndpointSuffix=core.windows.net")]
    [InlineData("https://acct.blob.core.windows.net/c?sv=2024&sig=AbCdEf123%3D&se=1", "https://acct.blob.core.windows.net/c?sv=2024&sig=<REDACTED>&se=1")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N", "<REDACTED>")]
    [InlineData("nothing to see here", "nothing to see here")]
    public void Redacts_built_in_secrets(string input, string expected) =>
        new Redactor().Redact(input).ShouldBe(expected);

    [Fact]
    public void Redacts_private_keys_even_when_truncated()
    {
        new Redactor().Redact("a\n-----BEGIN RSA PRIVATE KEY-----\nMIIEow").ShouldBe("a\n<REDACTED:private-key>");
    }

    [Theory]
    [InlineData("before <private>secret plan</private> after", "before <private-removed> after")]
    [InlineData("a <PRIVATE>x</PRIVATE> b <private>y</private> c", "a <private-removed> b <private-removed> c")]
    [InlineData("keep <private>everything after an unclosed tag", "keep <private-removed>")]
    [InlineData("no tags", "no tags")]
    public void Strips_private_spans(string input, string expected) =>
        Redactor.StripPrivate(input).ShouldBe(expected);

    [Fact]
    public void Applies_user_patterns_and_reports_invalid_ones()
    {
        var redactor = new Redactor([@"ACME-\d{4}", "(unclosed"]);

        redactor.Redact("ticket ACME-1234 done").ShouldBe("ticket <REDACTED> done");
        redactor.InvalidPatterns.ShouldBe(["(unclosed"]);
    }

    [Fact]
    public void Token_check_requires_bearer_scheme_and_exact_value()
    {
        WorkerToken.Matches("Bearer abc123", "abc123").ShouldBeTrue();
        WorkerToken.Matches("bearer abc123 ", "abc123").ShouldBeTrue();
        WorkerToken.Matches("Bearer", "abc123").ShouldBeFalse();
        WorkerToken.Matches("Bearer abc1234", "abc123").ShouldBeFalse();
        WorkerToken.Matches("abc123", "abc123").ShouldBeFalse();
        WorkerToken.Matches(null, "abc123").ShouldBeFalse();
    }

    [Fact]
    public void Token_is_created_once_and_reused()
    {
        using var temp = new TempDir();

        var first = WorkerToken.ReadOrCreate(temp.Path);
        var second = WorkerToken.ReadOrCreate(temp.Path);
        var rotated = WorkerToken.ReadOrCreate(temp.Path, rotate: true);

        first.Length.ShouldBe(64);
        second.ShouldBe(first);
        rotated.ShouldNotBe(first);
        WorkerToken.Read(temp.Path).ShouldBe(rotated);
    }
}
