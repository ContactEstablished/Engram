using System.Text.Json.Nodes;
using Engram.Cli.Spike;

namespace Engram.Tests.Spike;

public sealed class FixtureSanitizerTests
{
    private readonly FixtureSanitizer _sanitizer = new([("jdoe", "<USER>"), ("Jane Doe", "<NAME>"), ("DESKTOP-ABC123", "<HOST>")], maxStringLength: 200);

    [Theory]
    [InlineData(@"C:\Users\jdoe\src\app\Program.cs", @"<HOME>\src\app\Program.cs")]
    [InlineData(@"c:/Users/jdoe/src", "<HOME>/src")]
    [InlineData(@"{""file_path"":""C:\\Users\\jdoe\\src\\a.cs""}", @"{""file_path"":""<HOME>\\src\\a.cs""}")] // serialized JSON inside a string
    [InlineData("/mnt/c/Users/jdoe/src", "<HOME>/src")]
    [InlineData("/c/Users/jdoe/src", "<HOME>/src")] // Git Bash
    [InlineData("/home/jdoe/src/app", "<HOME>/src/app")]
    [InlineData("cd /home/someone-else && ls", "cd <HOME> && ls")]
    [InlineData("/Users/jdoe/Library", "<HOME>/Library")]
    public void Replaces_home_folders(string input, string expected)
    {
        _sanitizer.SanitizeString(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("whoami -> jdoe", "whoami -> <USER>")]
    [InlineData("JDOE wrote this", "<USER> wrote this")]
    [InlineData("jdoesmith is someone else", "jdoesmith is someone else")]
    [InlineData("Author: Jane Doe <jane@example.com>", "Author: <NAME> <<EMAIL>>")]
    [InlineData("host DESKTOP-ABC123 up", "host <HOST> up")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    public void Replaces_literal_terms_and_emails(string input, string expected)
    {
        _sanitizer.SanitizeString(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("OPENROUTER=sk-or-v1-0123456789abcdef0123456789abcdef")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ0123")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("Authorization: Bearer abcdefghijklmnop0123")]
    [InlineData("Server=db;User Id=sa;Password=Hunter2!xyz;")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=abc123def456==;")]
    [InlineData("slack xoxb-1234567890-abcdefghij")]
    public void Redacts_secrets(string input)
    {
        var output = _sanitizer.SanitizeString(input);

        output.ShouldContain("<REDACTED>");
        FixtureSanitizer.FindLeaks(output).ShouldBeEmpty();
    }

    [Fact]
    public void Redacts_private_key_blocks()
    {
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----";

        _sanitizer.SanitizeString("before\n" + pem + "\nafter").ShouldBe("before\n<REDACTED:private-key>\nafter");
    }

    [Fact]
    public void Truncates_long_strings()
    {
        var output = _sanitizer.SanitizeString(new string('x', 500));

        output.ShouldStartWith(new string('x', 200));
        output.ShouldEndWith("…<truncated 300 chars>");
    }

    [Fact]
    public void Sanitizes_nested_values_and_property_names_but_leaves_other_types_alone()
    {
        var node = JsonNode.Parse("""
            {"cwd":"C:\\Users\\jdoe\\repo","n":42,"ok":true,"nil":null,
             "files":{"C:\\Users\\jdoe\\a.txt":{"lines":["jdoe was here"]}}}
            """);

        var result = _sanitizer.Sanitize(node);

        var expected = JsonNode.Parse("""{"cwd":"<HOME>\\repo","n":42,"ok":true,"nil":null,"files":{"<HOME>\\a.txt":{"lines":["<USER> was here"]}}}""");
        JsonNode.DeepEquals(result, expected).ShouldBeTrue(result?.ToJsonString());
    }

    [Fact]
    public void Learns_usernames_from_paths()
    {
        FixtureSanitizer.FindUsernamesInPaths(@"C:\Users\alice\x and /home/bob/y and /mnt/d/Users/carol/z")
            .ShouldBe(["alice", "carol", "bob"], ignoreOrder: true);
    }

    [Fact]
    public void FindLeaks_ignores_placeholders()
    {
        FixtureSanitizer.FindLeaks(@"<HOME>\src <USER> <EMAIL> <REDACTED> /home/<USER>/x").ShouldBeEmpty();
    }
}
