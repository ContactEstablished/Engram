using System.Security.Cryptography;
using System.Text;

namespace Engram.Core.Security;

/// <summary>
/// The bearer token that HTTP hooks send to the worker. Stored in <c>~/.engram/token</c>; Claude Code reads
/// it from the <c>ENGRAM_TOKEN</c> environment variable (header interpolation via <c>allowedEnvVars</c>).
/// </summary>
public static class WorkerToken
{
    public const string EnvironmentVariable = "ENGRAM_TOKEN";

    public static string? Read(string home)
    {
        try
        {
            var path = EngramPaths.TokenFile(home);
            if (!File.Exists(path))
            {
                return null;
            }

            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns the existing token, or creates one (readable by the current user only on Linux).</summary>
    public static string ReadOrCreate(string home, bool rotate = false)
    {
        if (!rotate && Read(home) is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(home);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var path = EngramPaths.TokenFile(home);
        File.WriteAllText(path, token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return token;
    }

    /// <summary>Constant-time check of an <c>Authorization</c> header value against the token.</summary>
    public static bool Matches(string? authorizationHeader, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader.AsSpan(7).Trim().ToString());
        return CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(token));
    }
}
