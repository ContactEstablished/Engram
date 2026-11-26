using System.Text.RegularExpressions;

namespace Engram.Core.Security;

/// <summary>
/// Removes secrets and <c>&lt;private&gt;</c> spans from text before it is stored or sent to a summarizer.
/// Built-in rules cover private keys, provider API keys, GitHub/AWS/Slack tokens, JWTs, bearer tokens and
/// key=value secrets (connection strings, env files, CLI args). User patterns are added on top.
/// </summary>
public sealed partial class Redactor
{
    public const string Redacted = "<REDACTED>";
    public const string PrivateMarker = "<private-removed>";

    private static readonly TimeSpan UserPatternTimeout = TimeSpan.FromMilliseconds(200);
    private readonly Regex[] _userPatterns;

    /// <param name="userPatterns">Extra regexes; invalid ones are skipped and listed in <see cref="InvalidPatterns"/>.</param>
    public Redactor(IEnumerable<string>? userPatterns = null)
    {
        var valid = new List<Regex>();
        var invalid = new List<string>();
        foreach (var pattern in userPatterns ?? [])
        {
            try
            {
                valid.Add(new Regex(pattern, RegexOptions.CultureInvariant, UserPatternTimeout));
            }
            catch (ArgumentException)
            {
                invalid.Add(pattern);
            }
        }

        _userPatterns = [.. valid];
        InvalidPatterns = invalid;
    }

    public IReadOnlyList<string> InvalidPatterns { get; }

    /// <summary>Strips private spans, then redacts secrets.</summary>
    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = StripPrivate(value);
        s = RedactSecrets(s);
        foreach (var pattern in _userPatterns)
        {
            try
            {
                s = pattern.Replace(s, Redacted);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological user pattern must not stall capture; skip it for this value.
            }
        }

        return s;
    }

    /// <summary>
    /// Replaces <c>&lt;private&gt;…&lt;/private&gt;</c> spans. An unclosed tag hides everything after it,
    /// because guessing wrong would leak.
    /// </summary>
    public static string StripPrivate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOf("<private", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return value;
        }

        var s = PrivateSpanRegex().Replace(value, PrivateMarker);
        return UnclosedPrivateRegex().Replace(s, PrivateMarker);
    }

    /// <summary>Applies the built-in secret rules only.</summary>
    public static string RedactSecrets(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var s = PrivateKeyRegex().Replace(value, "<REDACTED:private-key>");
        s = AnthropicKeyRegex().Replace(s, Redacted);
        s = OpenAiStyleKeyRegex().Replace(s, Redacted);
        s = GitHubTokenRegex().Replace(s, Redacted);
        s = AwsKeyRegex().Replace(s, Redacted);
        s = SlackTokenRegex().Replace(s, Redacted);
        s = JwtRegex().Replace(s, Redacted);
        s = BearerRegex().Replace(s, "${prefix}" + Redacted);
        s = KeyValueSecretRegex().Replace(s, "${prefix}${quote}" + Redacted);
        return s;
    }

    /// <summary>Secrets the built-in rules would redact in <paramref name="text"/>. Used by leak tests.</summary>
    public static IEnumerable<string> FindSecrets(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Regex[] patterns = [PrivateKeyRegex(), AnthropicKeyRegex(), OpenAiStyleKeyRegex(), GitHubTokenRegex(), AwsKeyRegex(), SlackTokenRegex(), JwtRegex()];
        return patterns.SelectMany(p => p.Matches(text).Select(m => m.Value));
    }

    [GeneratedRegex(@"<private>[\s\S]*?</private>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateSpanRegex();

    [GeneratedRegex(@"<private>[\s\S]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnclosedPrivateRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKeyRegex();

    [GeneratedRegex(@"\bsk-(?:proj-|or-v1-|live_|test_)?[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStyleKeyRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<prefix>\bbearer\s+)[A-Za-z0-9._~+/\-=]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    // password=..., Pwd=..., AccountKey=..., SharedAccessKey=..., sig=... (Azure SAS), api_key: ..., token=...
    [GeneratedRegex(@"(?<prefix>\b(?:password|passwd|pwd|accountkey|sharedaccesskey|sig|client_secret|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\s*[=:]\s*)(?<quote>[""']?)[^;""'\s,}&]{4,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();
}
