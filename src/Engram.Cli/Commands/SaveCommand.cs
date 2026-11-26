using System.Globalization;
using System.Text;
using System.Text.Json;
using Engram.Cli.Worker;
using Engram.Core;
using Engram.Core.Projects;
using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Cli.Commands;

/// <summary>How <c>engram save</c> reaches the worker. Tests replace it with an in-process worker.</summary>
internal interface INoteTransport
{
    /// <summary>Whether the worker answers <c>/health</c>. <c>engram save</c> never starts it.</summary>
    bool WorkerRunning();

    /// <summary>POSTs the note to <c>/memory/note</c>; (null, null) when the worker didn't answer.</summary>
    (int? Status, string? Body) Send(byte[] json);
}

/// <summary>The real transport: the local worker on its configured port, with the token from <c>~/.engram/token</c>.</summary>
internal sealed class WorkerNoteTransport(int port, string? token) : INoteTransport
{
    public bool WorkerRunning() => WorkerClient.Get(port, "/health", TimeSpan.FromSeconds(2)).Status == 200;

    public (int? Status, string? Body) Send(byte[] json) => WorkerClient.PostForBody(port, "/memory/note", token, json, TimeSpan.FromSeconds(15));
}

/// <summary>
/// <c>engram save --folder DIR</c>: stores the text on stdin as a note in that project's memory (for Chorus: a Council
/// ruling). The worker stores it at once and has the summarizer condense it; the full text is kept. When the worker
/// isn't running, the note is spooled to <c>~/.engram/spool/notes</c> and stored when it next starts. Never prompts,
/// never starts the worker, never prints the token.
/// </summary>
internal static class SaveCommand
{
    /// <summary>The largest note, in UTF-8 bytes. Same limit as the worker's.</summary>
    public const int MaxBytes = 256 * 1024;

    public const string Help = """
        engram save --folder DIR [--title TEXT] [--type TYPE] [--source TEXT] [--json]  < note.txt

        Saves the text on stdin (UTF-8, up to 256 KiB) as a note in the memory of the project at DIR (a worktree's
        notes go to its repository's memory). The summarizer condenses it for search and session-start context; the
        full text stays available through `engram show ID` and get_observations. If the worker isn't running, the
        note is spooled and stored when it next starts. Never prompts and never starts the worker.

          --folder DIR     the project folder (required)
          --title TEXT     keep this title (default: the summary's title)
          --type TYPE      keep this type: decision, bugfix, feature, change, discovery, gotcha (default: the summarizer picks)
          --source TEXT    recorded with the note and shown with it, e.g. "chorus-council run 42"
          --json           one JSON object on stdout:
                           {"id","project","spooled","summarized":"queued|pending","title","type","warnings"}
                           or {"error"}
        Exit code: 0 when saved or spooled, 1 on an error.
        """;

    internal sealed record Options(string? Folder, string? Title, string? Type, string? Source, bool Json, bool Help);

    public static int Run(string[] args, Func<string, string?> getEnv, TextWriter output, TextWriter error) =>
        Run(args, getEnv, Console.OpenStandardInput(), !Console.IsInputRedirected, output, error, transport: null, TimeProvider.System);

    internal static Options Parse(string[] args)
    {
        string? folder = null, title = null, type = null, source = null;
        bool json = false, help = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--folder" or "-f":
                    folder = Value();
                    break;
                case "--title":
                    title = Value();
                    break;
                case "--type":
                    type = Value();
                    break;
                case "--source":
                    source = Value();
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}' (see engram save --help)");
            }
        }

        return new Options(folder, title, type, source, json, help);
    }

    internal static int Run(string[] args, Func<string, string?> getEnv, Stream stdin, bool stdinIsTerminal, TextWriter output, TextWriter error, INoteTransport? transport, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(time);
        var json = args.Contains("--json");
        try
        {
            var options = Parse(args);
            if (options.Help)
            {
                output.WriteLine(Help);
                return 0;
            }

            return Save(options, getEnv, stdin, stdinIsTerminal, output, error, transport, time);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message, json, output, error);
        }
        catch (JsonException)
        {
            return Fail("the worker took the note but its reply was unreadable; check engram recent", json, output, error);
        }
    }

    private static int Save(Options options, Func<string, string?> getEnv, Stream stdin, bool stdinIsTerminal, TextWriter output, TextWriter error, INoteTransport? transport, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.Folder))
        {
            throw new ArgumentException("--folder is required (the project the note belongs to)");
        }

        string? type = null;
        if (options.Type is not null && (type = ObservationTypes.Normalize(options.Type)) is null)
        {
            throw new ArgumentException($"unknown type '{options.Type}'; allowed: {string.Join(", ", ObservationTypes.All)}");
        }

        if (OperatingSystem.IsWindows() && (options.Folder.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase) || options.Folder.StartsWith("//wsl", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"{options.Folder} is inside WSL. Run engram save inside WSL (wsl.exe -e ...) so the note reaches the WSL worker.");
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Folder));
        if (!Directory.Exists(folder))
        {
            throw new ArgumentException($"{folder} is not a folder.");
        }

        if (stdinIsTerminal)
        {
            throw new ArgumentException("pass the note's text on stdin (engram save never prompts)");
        }

        var text = ReadNote(stdin);
        var project = ProjectIdentity.Resolve(folder);
        var home = EngramPaths.Home(getEnv);
        var (settings, _) = SettingsLoader.Load(home, getEnv);
        transport ??= new WorkerNoteTransport(settings.Worker.Port, WorkerToken.Read(home));
        var clientId = Guid.NewGuid().ToString("N");
        var request = Request(clientId, text, folder, options, type, project, time.GetUtcNow());
        var warnings = new List<string>();

        if (transport.WorkerRunning())
        {
            var (status, body) = transport.Send(request);
            if (status == 200 && body is not null)
            {
                return Saved(body, options.Json, output, error);
            }

            if (status == 400)
            {
                return Fail(ErrorText(body) ?? "the worker refused the note", options.Json, output, error);
            }

            warnings.Add(status is null
                ? "the worker didn't answer; the note was spooled instead"
                : string.Create(CultureInfo.InvariantCulture, $"the worker answered HTTP {status}{(status == 401 ? " (is ~/.engram/token the one it started with?)" : string.Empty)}; the note was spooled instead"));
        }

        string file;
        try
        {
            file = Spool(home, clientId, request, time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"the worker isn't running and the note could not be spooled: {ex.Message}", options.Json, output, error);
        }

        foreach (var warning in warnings)
        {
            error.WriteLine($"warning: {warning}");
        }

        if (options.Json)
        {
            output.WriteLine(EnableCommand.Json(w =>
            {
                w.WriteNull("id");
                w.WriteString("project", project.Identity);
                w.WriteBoolean("spooled", true);
                w.WriteString("summarized", "pending");
                NullableString(w, "title", options.Title?.Trim() is { Length: > 0 } t ? t : null);
                NullableString(w, "type", type);
                Strings(w, "warnings", warnings);
            }));
        }
        else
        {
            output.WriteLine($"The worker isn't running, so the note for {project.Identity} was spooled ({file}).");
            output.WriteLine("It is stored and summarized when the worker next starts (engram worker start, or the next Claude Code session).");
        }

        return 0;
    }

    /// <summary>Reads stdin as raw UTF-8 (never Console.In: the console code page would mangle it), within the limit.</summary>
    private static string ReadNote(Stream stdin)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stdin.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes + 3)
            {
                break;
            }
        }

        var bytes = buffer.ToArray().AsSpan();
        if (bytes.StartsWith("\xEF\xBB\xBF"u8))
        {
            bytes = bytes[3..];
        }

        if (bytes.Length > MaxBytes)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"the note is over the {MaxBytes / 1024} KiB limit ({MaxBytes} bytes of UTF-8)"));
        }

        var text = Encoding.UTF8.GetString(bytes).Trim();
        return text.Length > 0 ? text : throw new ArgumentException("the note is empty (pass its text on stdin)");
    }

    private static byte[] Request(string clientId, string text, string folder, Options options, string? type, ProjectInfo project, DateTimeOffset now)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("client_id", clientId);
            w.WriteString("text", text);
            w.WriteString("folder", folder);
            NullableString(w, "title", options.Title?.Trim() is { Length: > 0 } t ? t : null);
            NullableString(w, "type", type);
            NullableString(w, "source", options.Source?.Trim() is { Length: > 0 } s ? s : null);
            w.WriteString("saved_at", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartObject("project");
            w.WriteString("identity", project.Identity);
            w.WriteString("name", project.Name);
            NullableString(w, "branch", project.Branch);
            NullableString(w, "worktree", project.Worktree);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Writes the note atomically (temp file, then rename) so the worker never reads half of one.</summary>
    private static string Spool(string home, string clientId, byte[] request, TimeProvider time)
    {
        var dir = EngramPaths.NoteSpool(home);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture, $"{time.GetUtcNow():yyyyMMddHHmmssfff}-{clientId}");
        var temp = Path.Combine(dir, name + ".tmp");
        File.WriteAllBytes(temp, request);
        var file = Path.Combine(dir, name + ".json");
        File.Move(temp, file);
        return file;
    }

    /// <summary>Relays the worker's answer: as one JSON line with <c>spooled: false</c>, or as a sentence.</summary>
    private static int Saved(string body, bool json, TextWriter output, TextWriter error)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var warnings = root.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array
            ? ws.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : [];
        foreach (var warning in warnings)
        {
            error.WriteLine($"warning: {warning}");
        }

        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        if (json)
        {
            output.WriteLine(EnableCommand.Json(w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("project", S("project"));
                w.WriteBoolean("spooled", false);
                w.WriteString("summarized", S("summarized"));
                w.WriteString("title", S("title"));
                w.WriteString("type", S("type"));
                Strings(w, "warnings", warnings);
            }));
        }
        else
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Saved note #{id} ({S("type")}) for {S("project")}: {S("title")}"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The summarizer will condense it; the full text stays available with `engram show {id}`."));
        }

        return 0;
    }

    private static int Fail(string message, bool json, TextWriter output, TextWriter error)
    {
        error.WriteLine($"engram save: {message}");
        if (json)
        {
            output.WriteLine(EnableCommand.Json(w => w.WriteString("error", message)));
        }

        return 1;
    }

    private static string? ErrorText(string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? string.Empty);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void NullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static void Strings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
