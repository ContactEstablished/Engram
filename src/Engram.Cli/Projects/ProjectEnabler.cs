using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Engram.Core.Projects;

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
/// <param name="Folder">The folder.</param>
/// <param name="Actions">What changed (or would change), in words.</param>
/// <param name="Warnings">Things to know that did not stop the change.</param>
/// <param name="Files">Every file that changed (or would change).</param>
internal sealed record ProjectChange(string Folder, IReadOnlyList<string> Actions, IReadOnlyList<string> Warnings, IReadOnlyList<string> Files);

/// <summary>Read-only view of a folder, for <c>engram state</c>.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Enabled">Engram's hooks are present.</param>
/// <param name="Mcp"><c>local</c>, <c>file</c> or <c>none</c>.</param>
/// <param name="HooksOk">All of Engram's hooks are present and match what <c>enable</c> would write now.</param>
/// <param name="Problems">Why <paramref name="HooksOk"/> is false, or why the settings file can't be read.</param>
/// <param name="WorktreeOf">The main working tree, when this folder is a linked git worktree.</param>
/// <param name="Project">The identity Engram keys memory on (normalized git remote, else repo or folder).</param>
internal sealed record ProjectState(string Folder, bool Enabled, string Mcp, bool HooksOk, IReadOnlyList<string> Problems, string? WorktreeOf, string Project);

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

    public (int ExitCode, string Output) Claude(string dir, params string[] args) =>
        LocateClaude() is { } claude ? Run(claude, dir, args) : (-1, "claude not found on PATH or in ~/.local/bin");

    /// <summary>
    /// PATH first, then the native installer's location: under <c>wsl.exe -e</c> (no login shell) ~/.local/bin isn't on PATH.
    /// </summary>
    internal static string? LocateClaude()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe" } : ["claude"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", names[0]);
        return File.Exists(local) ? local : null;
    }

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
/// git through <c>.git/info/exclude</c> (a local file, so no tracked file changes). Idempotent. Also the one place
/// that knows how to tell whether a folder is enabled (<see cref="State"/>).
/// </summary>
internal sealed class ProjectEnabler(IProjectTools tools, string home, int port, string toolMatcher, TimeProvider time)
{
    public const string SettingsRelative = ".claude/settings.local.json";
    public const string McpRelative = ".mcp.json";

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// What hooks and the MCP registration run. On Windows the bare name (on the user PATH via the installer). On
    /// Linux/WSL the absolute path of this binary: Claude Code started without a login shell (e.g. `wsl.exe -e`)
    /// doesn't have ~/.local/bin on PATH.
    /// </summary>
    internal static string EngramCommand { get; } =
        !OperatingSystem.IsWindows() && Environment.ProcessPath is { } self && Path.GetFileName(self) == "engram" ? self : "engram";

    /// <summary>
    /// Every working tree of the repo that <paramref name="folder"/> belongs to: the folder itself first, then the
    /// others, each with the main working tree it belongs to (null for the main one and for non-git folders).
    /// </summary>
    public IReadOnlyList<(string Folder, string? WorktreeOf)> ExpandWorktrees(string folder)
    {
        var trees = ListWorktrees(folder);
        if (trees.Count == 0)
        {
            return [(folder, null)];
        }

        var main = trees[0];
        var result = new List<(string, string?)> { (folder, PathComparer.Equals(Trim(folder), Trim(main)) ? null : main) };
        foreach (var tree in trees.Where(t => !PathComparer.Equals(Trim(t), Trim(folder))))
        {
            result.Add((tree, PathComparer.Equals(Trim(tree), Trim(main)) ? null : main));
        }

        return result;
    }

    public ProjectChange Enable(string folder, McpMode mcp, bool dryRun)
    {
        CheckFolder(folder);
        var actions = new List<string>();
        var warnings = new List<string>();
        var files = new List<string>();
        WarnIfNotRepoRoot(folder, warnings);

        var settingsPath = Path.Combine(folder, ".claude", "settings.local.json");
        var root = ReadJson(settingsPath, out var existed);
        var before = root.ToJsonString();
        var previousPort = HookSettings.RegisteredPort(root);
        HookSettings.Apply(root, port, toolMatcher, EngramCommand);
        if (existed && before == root.ToJsonString())
        {
            actions.Add($"hooks already registered in {SettingsRelative} (port {port}), no change");
        }
        else
        {
            files.Add(settingsPath);
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
                WriteMcpFile(folder, dryRun, actions, files);
                break;
        }

        KeepOutOfGit(folder, exclude, dryRun, actions, warnings, files);
        if (!dryRun)
        {
            ProjectRegistry.Set(home, folder, mcp switch { McpMode.Local => "local", McpMode.File => "file", _ => "none" });
        }

        return new ProjectChange(folder, actions, warnings, files);
    }

    public ProjectChange Disable(string folder, bool dryRun)
    {
        CheckFolder(folder);
        var actions = new List<string>();
        var warnings = new List<string>();
        var files = new List<string>();

        var settingsPath = Path.Combine(folder, ".claude", "settings.local.json");
        if (File.Exists(settingsPath))
        {
            var root = ReadJson(settingsPath, out _);
            var removed = HookSettings.Remove(root);
            if (removed == 0)
            {
                actions.Add($"no Engram hooks in {SettingsRelative}");
            }
            else
            {
                files.Add(settingsPath);
                actions.Add(root.Count == 0
                    ? $"remove {SettingsRelative} (it held only Engram's hooks)"
                    : $"remove {removed} Engram hook(s) from {SettingsRelative} (other settings kept)");
                if (!dryRun)
                {
                    Backup(settingsPath, existed: true);
                    if (root.Count == 0)
                    {
                        File.Delete(settingsPath);
                    }
                    else
                    {
                        WriteJson(settingsPath, root);
                    }
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

                files.Add(mcpPath);
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

        if (dryRun)
        {
            actions.Add("remove the engram MCP server from Claude Code's local scope for this project (if present)");
        }
        else
        {
            var (exit, output) = tools.Claude(folder, "mcp", "remove", "engram", "--scope", "local");
            if (exit == 0)
            {
                actions.Add("removed the engram MCP server (local scope)");
            }
            else if (exit == -1)
            {
                warnings.Add("couldn't run `claude` to remove the MCP server: " + output);
            }

            ProjectRegistry.Remove(home, folder);
        }

        return new ProjectChange(folder, actions, warnings, files);
    }

    /// <summary>Whether Engram is on for <paramref name="folder"/>, and whether its hooks are complete and current. Never writes.</summary>
    public ProjectState State(string folder)
    {
        CheckFolder(folder);
        var problems = new List<string>();
        var enabled = false;
        var hooksOk = false;
        var settingsPath = Path.Combine(folder, ".claude", "settings.local.json");
        try
        {
            var actual = ReadJson(settingsPath, out _);
            var registered = HookSettings.RegisteredEvents(actual);
            enabled = registered.Count > 0;
            if (enabled)
            {
                var expected = new JsonObject();
                HookSettings.Apply(expected, port, toolMatcher, EngramCommand);
                foreach (var name in HookSettings.Events)
                {
                    var want = EngramGroups(expected, name);
                    var have = EngramGroups(actual, name);
                    if (have.Count == 0)
                    {
                        problems.Add($"missing the {name} hook");
                    }
                    else if (have.Count > 1 || have[0] != want[0])
                    {
                        problems.Add($"the {name} hook differs from what enable writes (old port {HookSettings.RegisteredPort(actual)?.ToString(CultureInfo.InvariantCulture) ?? "?"} vs {port}, a changed matcher, or edited by hand)");
                    }
                }

                hooksOk = problems.Count == 0;
            }
        }
        catch (ArgumentException ex)
        {
            problems.Add(ex.Message);
        }

        var mcp = McpInFile(folder) ? "file" : ProjectRegistry.Get(home, folder) == "local" ? "local" : "none";
        var trees = ListWorktrees(folder);
        var worktreeOf = trees.Count > 0 && !PathComparer.Equals(Trim(trees[0]), Trim(folder)) ? trees[0] : null;
        return new ProjectState(folder, enabled, mcp, hooksOk, problems, worktreeOf, ProjectIdentity.Resolve(folder).Identity);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static string Trim(string path) => Path.GetFullPath(path).TrimEnd('/', '\\');

    /// <summary>Engram's groups for one event, serialized, so two settings files can be compared.</summary>
    private static List<string> EngramGroups(JsonObject root, string name) =>
        root["hooks"]?[name] is JsonArray groups
            ? [.. groups.OfType<JsonObject>()
                .Where(g => g["hooks"] is JsonArray h && h.OfType<JsonObject>().Any(HookSettings.IsEngram))
                .Select(g => g.ToJsonString())]
            : [];

    private static bool McpInFile(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ".mcp.json");
            return File.Exists(path) && ReadJson(path, out _)["mcpServers"]?["engram"] is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The repo's working trees, main first (paths as git prints them, made absolute). Empty outside git.</summary>
    private List<string> ListWorktrees(string folder)
    {
        var listing = tools.Git(folder, "worktree", "list", "--porcelain");
        if (listing is null)
        {
            return [];
        }

        var trees = new List<string>();
        string? current = null;
        var skip = false;
        foreach (var line in listing.Split('\n', StringSplitOptions.TrimEntries).Append(string.Empty))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                current = line[9..];
                skip = false;
            }
            else if (line == "bare" || line.StartsWith("prunable", StringComparison.Ordinal))
            {
                skip = true;
            }
            else if (line.Length == 0 && current is not null)
            {
                if (!skip && Directory.Exists(current))
                {
                    trees.Add(Path.GetFullPath(current));
                }

                current = null;
            }
        }

        return trees;
    }

    private static void CheckFolder(string folder)
    {
        if (folder.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("//wsl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{folder} is inside WSL. Run engram inside WSL (wsl.exe -e ...) so the hooks use the WSL worker's port.");
        }

        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"{folder} is not a folder.");
        }
    }

    private void WarnIfNotRepoRoot(string folder, List<string> warnings)
    {
        if (tools.Git(folder, "rev-parse", "--show-toplevel")?.Trim() is { Length: > 0 } top
            && !PathComparer.Equals(Trim(top), Trim(folder)))
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

    internal static void WriteJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            root.WriteTo(writer);
        }

        var temp = path + ".engram-tmp";
        File.WriteAllText(temp, Encoding.UTF8.GetString(buffer.ToArray()) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
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

        var (exit, output) = tools.Claude(folder, "mcp", "add", "--scope", "local", "engram", "--", EngramCommand, "mcp");
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
            warnings.Add($"couldn't register the MCP server through `claude mcp add` ({(exit == -1 ? output : output.Split('\n')[0])}); retry with --mcp file");
        }
    }

    private static void WriteMcpFile(string folder, bool dryRun, List<string> actions, List<string> files)
    {
        var path = Path.Combine(folder, ".mcp.json");
        var root = ReadJson(path, out var existed);
        var servers = root["mcpServers"] as JsonObject ?? [];
        root["mcpServers"] = servers;
        var wanted = new JsonObject { ["command"] = EngramCommand, ["args"] = new JsonArray("mcp") };
        if (servers["engram"]?.ToJsonString() == wanted.ToJsonString())
        {
            actions.Add($"{McpRelative} already has the engram server");
            return;
        }

        servers["engram"] = wanted;
        files.Add(path);
        actions.Add(existed ? $"add the engram server to the existing {McpRelative}" : $"create {McpRelative} with the engram server (Claude Code asks you to approve it once)");
        if (!dryRun)
        {
            WriteJson(path, root);
        }
    }

    /// <summary>Adds our files to <c>.git/info/exclude</c> (never to a tracked .gitignore) unless git already ignores them.</summary>
    private void KeepOutOfGit(string folder, List<string> relativePaths, bool dryRun, List<string> actions, List<string> warnings, List<string> files)
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

        var full = Path.GetFullPath(Path.IsPathRooted(excludePath) ? excludePath : Path.Combine(folder, excludePath));
        files.Add(full);
        actions.Add($"add {string.Join(" and ", missing)} to .git/info/exclude (local to this clone; no tracked file changes)");
        if (dryRun)
        {
            return;
        }

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

/// <summary>
/// <c>~/.engram/projects.json</c>: the folders Engram was enabled in and how the MCP server was registered. Engram's
/// own record, so <c>engram state</c> doesn't have to ask Claude Code (slow) or read its config files.
/// </summary>
internal static class ProjectRegistry
{
    private static string FilePath(string home) => Path.Combine(home, "projects.json");

    private static string Key(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('/', '\\');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    public static string? Get(string home, string folder) =>
        Load(home)[Key(folder)]?["mcp"]?.GetValue<string>();

    public static void Set(string home, string folder, string mcp)
    {
        var root = Load(home);
        root[Key(folder)] = new JsonObject
        {
            ["folder"] = Path.GetFullPath(folder),
            ["mcp"] = mcp,
            ["enabled_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        Save(home, root);
    }

    public static void Remove(string home, string folder)
    {
        var root = Load(home);
        if (root.Remove(Key(folder)))
        {
            Save(home, root);
        }
    }

    private static JsonObject Load(string home)
    {
        try
        {
            return File.Exists(FilePath(home)) ? JsonNode.Parse(File.ReadAllText(FilePath(home))) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(string home, JsonObject root)
    {
        Directory.CreateDirectory(home);
        ProjectEnabler.WriteJson(FilePath(home), root);
    }
}
