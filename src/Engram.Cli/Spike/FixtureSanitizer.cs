using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Engram.Cli.Spike;

/// <summary>
/// Replaces personal data in captured payloads before they become committed test fixtures: home
/// folders, usernames, emails and secrets become placeholders, and long strings are truncated.
/// Works on parsed JSON (values and property names), so escaping never hides a match.
/// </summary>
internal sealed partial class FixtureSanitizer
{
    public const string Home = "<HOME>";
    public const string User = "<USER>";
    public const string Email = "<EMAIL>";
    public const string Redacted = "<REDACTED>";

    private readonly List<(Regex Pattern, string Replacement)> _literals = [];
    private readonly int _maxStringLength;

    /// <param name="literals">Exact terms (usernames, real names, host names, tokens) and their placeholders.</param>
    /// <param name="maxStringLength">Longer strings are cut to this length plus a marker.</param>
    public FixtureSanitizer(IEnumerable<(string Term, string Replacement)> literals, int maxStringLength = 2000)
    {
        _maxStringLength = maxStringLength;
        foreach (var (term, replacement) in literals
                     .Where(l => !string.IsNullOrWhiteSpace(l.Term) && l.Term.Trim().Length >= 3)
                     .DistinctBy(l => l.Term.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(l => l.Term.Length))
        {
            var escaped = Regex.Escape(term.Trim());
            _literals.Add((new Regex($"(?<![A-Za-z0-9]){escaped}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
        }
    }

    /// <summary>Usernames that appear in home-folder paths in <paramref name="text"/>.</summary>
    public static IEnumerable<string> FindUsernamesInPaths(string text)
    {
        foreach (Match m in WindowsHomeRegex().Matches(text))
        {
            yield return m.Groups["user"].Value;
        }

        foreach (Match m in UnixHomeRegex().Matches(text))
        {
            yield return m.Groups["user"].Value;
        }
    }

    public string SanitizeString(string value)
    {
        var s = PrivateKeyRegex().Replace(value, "<REDACTED:private-key>");
        s = AnthropicKeyRegex().Replace(s, Redacted);
        s = OpenAiStyleKeyRegex().Replace(s, Redacted);
        s = GitHubTokenRegex().Replace(s, Redacted);
        s = AwsKeyRegex().Replace(s, Redacted);
        s = SlackTokenRegex().Replace(s, Redacted);
        s = JwtRegex().Replace(s, Redacted);
        s = BearerRegex().Replace(s, "${prefix}" + Redacted);
        s = KeyValueSecretRegex().Replace(s, "${prefix}${quote}" + Redacted);
        s = EmailRegex().Replace(s, m => m.Value.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ? m.Value : Email);
        s = WindowsHomeRegex().Replace(s, Home);
        s = UnixHomeRegex().Replace(s, Home);
        foreach (var (pattern, replacement) in _literals)
        {
            s = pattern.Replace(s, replacement);
        }

        return s.Length > _maxStringLength
            ? string.Concat(s.AsSpan(0, _maxStringLength), $"…<truncated {s.Length - _maxStringLength} chars>")
            : s;
    }

    public JsonNode? Sanitize(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => SanitizeObject(obj),
        JsonArray array => new JsonArray([.. array.Select(Sanitize)]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(SanitizeString(value.GetValue<string>())),
        _ => node.DeepClone(),
    };

    private JsonObject SanitizeObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var (key, child) in obj)
        {
            var newKey = SanitizeString(key);
            // Two keys could collapse to the same placeholder; keep the first.
            result.TryAdd(newKey, Sanitize(child));
        }

        return result;
    }

    /// <summary>
    /// Personal data still present in <paramref name="text"/>. Used by tests to guard committed fixtures;
    /// placeholders don't count.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(string text)
    {
        var leaks = new List<string>();
        Regex[] patterns =
        [
            PrivateKeyRegex(), AnthropicKeyRegex(), OpenAiStyleKeyRegex(), GitHubTokenRegex(), AwsKeyRegex(),
            SlackTokenRegex(), JwtRegex(), WindowsHomeRegex(), UnixHomeRegex(),
        ];
        foreach (var pattern in patterns)
        {
            leaks.AddRange(pattern.Matches(text).Select(m => m.Value));
        }

        leaks.AddRange(EmailRegex().Matches(text).Select(m => m.Value).Where(v => !v.StartsWith("git@", StringComparison.OrdinalIgnoreCase)));
        return leaks;
    }

    // C:\Users\name, C:/Users/name, C:\\Users\\name (serialized JSON inside a string), /c/Users/name (Git Bash),
    // /mnt/c/Users/name (WSL view of Windows). Placeholders like <USER> are not names.
    [GeneratedRegex(@"(?:\b[A-Za-z]:|(?<![A-Za-z0-9])/(?:mnt/)?[A-Za-z])(?:\\+|/)Users(?:\\+|/)(?<user>[^\\/""'\s:*?<>|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsHomeRegex();

    // /home/name, /Users/name (macOS). Must not re-match the tail of a Windows path already replaced.
    [GeneratedRegex(@"(?<![A-Za-z0-9>])/(?:home|Users)/(?<user>[^/""'\s<>:]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHomeRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
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

    // password=..., Pwd=..., AccountKey=..., api_key: ..., secret=..., token=... (connection strings, env files, CLI args)
    [GeneratedRegex(@"(?<prefix>\b(?:password|passwd|pwd|accountkey|sharedaccesskey|client_secret|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\s*[=:]\s*)(?<quote>[""']?)[^;""'\s,}]{4,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
