using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engram.Cli.Projects;

internal enum McpMode
{
    /// <summary>Registered for this project only, in Claude Code's own config, through <c>claude mcp add --scope local</c>: nothing is added to the repo.</summary>
    Local,

    /// <summary>A <c>.mcp.json</c> in the project folder (Claude Code asks you to approve it once).</summary>
    File,

    None,
}

/// <summary>What happened (or, in a dry run, would happen) in one folder.</summary>
internal sealed record ProjectChange(string Folder, IReadOnlyList<string> Actions, IReadOnlyList<string> Warnings);

/// <summary>Git and Claude Code, behind an interface so enable/disable can be tested without either.</summary>
internal interface IProjectTools
{
    /// <summary>stdout of <c>git -C dir args</c>, or null when git fails or isn't there.</summary>
    string? Git(string dir, params string[] args);

    /// <summary>Runs <c>claude args</c> in <paramref name="dir"/>. Exit code -1 means claude could not be run.</summary>
    (int ExitCode, string Output) Claude(string dir, params string[] args);
}

internal sealed class SystemProjectTools : IProjectTools
{
    public string? Git(string dir, params string[] args)
    {
        var (exit, output) = Run("git", dir, ["-C", dir, .. args]);
        return exit == 0 ? output : null;
    }

    public (int ExitCode, string Output) Claude(string dir, params string[] args) => Run("claude", dir, args);

    private static (int ExitCode, string Output) Run(string file, string dir, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            return (process.ExitCode, (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }
}

/// <summary>
/// Turns Engram on or off for a project folder: merges the hooks into <c>.claude/settings.local.json</c> (never
/// overwriting what's there; the original is backed up first), registers the MCP server, and keeps both files out of
/// git through <c>.git/info/exclude</c> (a local file, so no tracked file changes). Idempotent.
/// </summary>
internal sealed class ProjectEnabler(IProjectTools tools, string home, int port, string toolMatcher, TimeProvider time)
{
    public const string SettingsRelative = ".claude/settings.local.json";
    public const string McpRelative = ".mcp.json";

    /// <summary>Every working tree of the repo that <paramref name="folder"/> belongs to (including itself), or just the folder.</summary>
    public IReadOnlyList<string> ExpandWorktrees(string folder)
    {
        var listing = tools.Git(folder, "worktree", "list", "--porcelain");
        if (listing is null)
        {
            return [folder];
        }

        var trees = new List<string>();
        string? current = null;
        foreach (var line in listing.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line[9..];
            }
            else if (line is "bare" or "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                current = null;
            }
            else if (line.Length == 0 && current is not null)
            {
                trees.Add(current);
                current = null;
            }
        }

        if (current is not null)
        {
            trees.Add(current);
        }

        return [.. trees.Select(Path.GetFullPath).Where(Directory.Exists).Prepend(folder).Distinct(PathComparer)];
    }

    public ProjectChange Enable(string folder, McpMode mcp, bool dryRun)
    {
        CheckFolder(folder);
        var actions = new List<string>();
        var warnings = new List<string>();
        WarnIfNotRepoRoot(folder, warnings);

        var settingsPath = Path.Combine(folder, ".claude", "settings.local.json");
        var root = ReadJson(settingsPath, out var existed);
        var before = root.ToJsonString();
        var previousPort = HookSettings.RegisteredPort(root);
        HookSettings.Apply(root, port, toolMatcher);
        if (existed && before == root.ToJsonString())
        {
            actions.Add($"hooks already registered in {SettingsRelative} (port {port}), no change");
        }
        else
        {
            actions.Add(existed
                ? $"merge Engram's hooks into the existing {SettingsRelative} (other settings kept; backup in ~/.engram/backups/enable)"
                : $"create {SettingsRelative} with Engram's hooks");
            if (previousPort is { } p && p != port)
            {
                actions.Add($"hook port changed {p} -> {port}");
            }

            if (!dryRun)
            {
                Backup(settingsPath, existed);
                WriteJson(settingsPath, root);
            }
        }

        var exclude = new List<string> { SettingsRelative };
        switch (mcp)
        {
            case McpMode.Local:
                RegisterMcpLocal(folder, dryRun, actions, warnings);
                break;
            case McpMode.File:
                exclude.Add(McpRelative);
                WriteMcpFile(folder, dryRun, actions);
                break;
        }

        KeepOutOfGit(folder, exclude, dryRun, actions, warnings);
        return new ProjectChange(folder, actions, warnings);
    }

    public ProjectChange Disable(string folder, bool dryRun)
    {
        CheckFolder(folder);
        var actions = new List<string>();
        var warnings = new List<string>();

        var settingsPath = Path.Combine(folder, ".claude", "settings.local.json");
        if (File.Exists(settingsPath))
        {
            var root = ReadJson(settingsPath, out _);
            var removed = HookSettings.Remove(root);
            if (removed == 0)
            {
                actions.Add($"no Engram hooks in {SettingsRelative}");
            }
            else if (root.Count == 0)
            {
                actions.Add($"remove {SettingsRelative} (it held only Engram's hooks)");
                if (!dryRun)
                {
                    Backup(settingsPath, existed: true);
                    File.Delete(settingsPath);
                }
            }
            else
            {
                actions.Add($"remove {removed} Engram hook(s) from {SettingsRelative} (other settings kept)");
                if (!dryRun)
                {
                    Backup(settingsPath, existed: true);
                    WriteJson(settingsPath, root);
                }
            }
        }
        else
        {
            actions.Add($"no {SettingsRelative}");
        }

        var mcpPath = Path.Combine(folder, ".mcp.json");
        if (File.Exists(mcpPath))
        {
            var mcpRoot = ReadJson(mcpPath, out _);
            if (mcpRoot["mcpServers"] is JsonObject servers && servers.Remove("engram"))
            {
                if (servers.Count == 0)
                {
                    mcpRoot.Remove("mcpServers");
                }

                actions.Add(mcpRoot.Count == 0 ? $"remove {McpRelative} (it held only Engram)" : $"remove the engram server from {McpRelative}");
                if (!dryRun)
                {
                    if (mcpRoot.Count == 0)
                    {
                        File.Delete(mcpPath);
                    }
                    else
                    {
                        WriteJson(mcpPath, mcpRoot);
                    }
                }
            }
        }

        var (exit, output) = dryRun ? (0, string.Empty) : tools.Claude(folder, "mcp", "remove", "engram", "--scope", "local");
        if (dryRun)
        {
            actions.Add("remove the engram MCP server from Claude Code's local scope for this project (if present)");
        }
        else if (exit == 0)
        {
            actions.Add("removed the engram MCP server (local scope)");
        }
        else if (exit == -1)
        {
            warnings.Add("couldn't run `claude` to remove the MCP server: " + output);
        }

        return new ProjectChange(folder, actions, warnings);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void CheckFolder(string folder)
    {
        if (folder.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("//wsl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{folder} is inside WSL. Run `engram enable` from inside WSL so the hooks use the WSL worker's port.");
        }

        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"{folder} is not a folder.");
        }
    }

    private void WarnIfNotRepoRoot(string folder, List<string> warnings)
    {
        if (tools.Git(folder, "rev-parse", "--show-toplevel")?.Trim() is { Length: > 0 } top
            && !PathComparer.Equals(Path.GetFullPath(top).TrimEnd('/', '\\'), Path.GetFullPath(folder).TrimEnd('/', '\\')))
        {
            warnings.Add($"{folder} is inside the repo at {top}. Claude Code reads these settings only when started in this folder.");
        }
    }

    /// <summary>Reads a settings file. Refuses (rather than damages) files with comments or invalid JSON.</summary>
    private static JsonObject ReadJson(string path, out bool existed)
    {
        existed = File.Exists(path);
        if (!existed)
        {
            return [];
        }

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException($"{path} is not a JSON object; fix it by hand first.");
        }
        catch (JsonException ex)
        {
            var lenient = false;
            try
            {
                lenient = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not null;
            }
            catch (JsonException)
            {
                // Really invalid.
            }

            throw new ArgumentException(lenient
                ? $"{path} has comments or trailing commas; rewriting it would drop them. Add Engram's hooks by hand (see hooks/README.md) or remove the comments."
                : $"{path} is not valid JSON ({ex.Message.Split('.')[0]}); fix it by hand first.");
        }
    }

    private static void WriteJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            root.WriteTo(writer);
        }

        File.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n", new UTF8Encoding(false));
    }

    private void Backup(string path, bool existed)
    {
        if (!existed)
        {
            return;
        }

        var dir = Path.Combine(home, "backups", "enable");
        Directory.CreateDirectory(dir);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = string.Concat(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) ?? "project", "-", Path.GetFileName(path), "-", stamp, ".bak");
        File.Copy(path, Path.Combine(dir, name), overwrite: true);
    }

    private void RegisterMcpLocal(string folder, bool dryRun, List<string> actions, List<string> warnings)
    {
        if (dryRun)
        {
            actions.Add("register the engram MCP server for this project through `claude mcp add --scope local` (nothing is added to the repo)");
            return;
        }

        var (exit, output) = tools.Claude(folder, "mcp", "add", "--scope", "local", "engram", "--", "engram", "mcp");
        if (exit == 0)
        {
            actions.Add("registered the engram MCP server for this project (Claude Code local scope)");
        }
        else if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add("the engram MCP server is already registered for this project");
        }
        else
        {
            warnings.Add($"couldn't register the MCP server through `claude mcp add` ({(exit == -1 ? "claude not found" : output)}); retry with --mcp file");
        }
    }

    private static void WriteMcpFile(string folder, bool dryRun, List<string> actions)
    {
        var path = Path.Combine(folder, ".mcp.json");
        var root = ReadJson(path, out var existed);
        var servers = root["mcpServers"] as JsonObject ?? [];
        root["mcpServers"] = servers;
        var wanted = new JsonObject { ["command"] = "engram", ["args"] = new JsonArray("mcp") };
        if (servers["engram"]?.ToJsonString() == wanted.ToJsonString())
        {
            actions.Add($"{McpRelative} already has the engram server");
            return;
        }

        servers["engram"] = wanted;
        actions.Add(existed ? $"add the engram server to the existing {McpRelative}" : $"create {McpRelative} with the engram server (Claude Code asks you to approve it once)");
        if (!dryRun)
        {
            WriteJson(path, root);
        }
    }

    /// <summary>Adds our files to <c>.git/info/exclude</c> (never to a tracked .gitignore) unless git already ignores them.</summary>
    private void KeepOutOfGit(string folder, List<string> relativePaths, bool dryRun, List<string> actions, List<string> warnings)
    {
        if (tools.Git(folder, "rev-parse", "--git-dir") is null)
        {
            return; // not a git repo: nothing to keep out of
        }

        var missing = new List<string>();
        foreach (var relative in relativePaths)
        {
            if (tools.Git(folder, "ls-files", "--error-unmatch", relative) is not null)
            {
                warnings.Add($"{relative} is tracked by git, so Engram's changes will show up as a modification.");
                continue;
            }

            if (tools.Git(folder, "check-ignore", "-q", relative) is null)
            {
                missing.Add(relative);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        var excludePath = tools.Git(folder, "rev-parse", "--git-path", "info/exclude")?.Trim();
        if (string.IsNullOrEmpty(excludePath))
        {
            warnings.Add($"couldn't find .git/info/exclude; add {string.Join(" and ", missing)} to a .gitignore yourself.");
            return;
        }

        actions.Add($"add {string.Join(" and ", missing)} to .git/info/exclude (local to this clone; no tracked file changes)");
        if (dryRun)
        {
            return;
        }

        var full = Path.IsPathRooted(excludePath) ? excludePath : Path.Combine(folder, excludePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var existing = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
        var lines = existing.Split('\n', StringSplitOptions.TrimEntries);
        var add = missing.Where(m => !lines.Contains(m)).ToList();
        if (add.Count > 0)
        {
            File.AppendAllText(full, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty) + "# Engram (local files)\n" + string.Join('\n', add) + "\n");
        }
    }
}
