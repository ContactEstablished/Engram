using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Engram.Core.Projects;

/// <param name="Identity">Stable key shared by every worktree and clone of a repo.</param>
/// <param name="Name">Short display name.</param>
/// <param name="Worktree">The git toplevel for this cwd (null outside git).</param>
/// <param name="Branch">Current branch (null outside git or when detached).</param>
public sealed record ProjectInfo(string Identity, string Name, string? Worktree, string? Branch);

/// <summary>
/// Resolves which project a cwd belongs to. Keying on cwd would split one repo into many memories
/// (a worktree per branch, ephemeral Claude worktrees), so the identity is the normalized remote URL,
/// else the git common dir, else the folder itself.
/// </summary>
/// <remarks>Shared by the worker (capture, search) and the CLI (<c>engram state</c>), so both agree on identity.</remarks>
public static partial class ProjectIdentity
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    public static ProjectInfo Resolve(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        if (!Directory.Exists(cwd))
        {
            return FolderProject(cwd);
        }

        var revParse = RunGit(cwd, "rev-parse", "--path-format=absolute", "--git-common-dir", "--show-toplevel", "--abbrev-ref", "HEAD");
        if (revParse is null)
        {
            return FolderProject(cwd);
        }

        var lines = revParse.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return FolderProject(cwd);
        }

        var commonDir = lines[0];
        var toplevel = lines[1];
        var branch = lines.Length > 2 && lines[2] != "HEAD" ? lines[2] : null;

        var remote = RunGit(cwd, "config", "--get", "remote.origin.url")?.Trim();
        if (!string.IsNullOrEmpty(remote) && NormalizeRemote(remote) is { } normalized)
        {
            return new ProjectInfo(normalized, LastSegment(normalized), toplevel, branch);
        }

        // No remote: the common dir is shared by all worktrees of the repo.
        var repoDir = NormalizePath(commonDir);
        if (repoDir.EndsWith("/.git", StringComparison.Ordinal))
        {
            repoDir = repoDir[..^5];
        }

        return new ProjectInfo("local:" + repoDir, LastSegment(repoDir), toplevel, branch);
    }

    private static ProjectInfo FolderProject(string cwd)
    {
        var path = NormalizePath(cwd);
        return new ProjectInfo("dir:" + path, LastSegment(path), null, null);
    }

    /// <summary>
    /// <c>git@github.com:Org/Repo.git</c>, <c>https://user@github.com/org/repo</c> and
    /// <c>ssh://git@github.com:22/org/repo.git</c> all become <c>github.com/org/repo</c>.
    /// </summary>
    public static string? NormalizeRemote(string remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var r = remote.Trim();
        string host;
        string path;
        var scp = ScpRemoteRegex().Match(r);
        if (scp.Success)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(r, UriKind.Absolute, out var uri) && !uri.IsFile && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // A local path used as a remote.
            return string.IsNullOrEmpty(r) ? null : "local:" + NormalizePath(r);
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return path.Length == 0 ? null : $"{host}/{path}".ToLowerInvariant();
    }

    /// <summary>Forward slashes, no trailing slash, case-folded on Windows (case-insensitive file system).</summary>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        full = full.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private static string LastSegment(string value)
    {
        var trimmed = value.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i >= 0 ? trimmed[(i + 1)..] : trimmed;
    }

    private static string? RunGit(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(cwd);
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            // Never let git prompt or pick up the hook environment's pager.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // git not installed or not runnable
        }
    }

    // user@host:path (scp-like), but not C:\path or C:/path.
    [GeneratedRegex(@"^(?:[^@/\\]+@)?(?<host>[A-Za-z0-9.\-]{2,}):(?!//)(?<path>[^\\].*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemoteRegex();
}
