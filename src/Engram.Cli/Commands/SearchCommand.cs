using System.Globalization;
using System.Text.Json;
using Engram.Cli.Mcp;

namespace Engram.Cli.Commands;

/// <summary>
/// <c>engram search &lt;words&gt; [--all] [--type T] [--since 14d] [--limit N]</c> and <c>engram show &lt;id&gt;...</c>:
/// the MCP search and get_observations tools, for you in a terminal.
/// </summary>
internal static class SearchCommand
{
    public static int Search(string[] args, IMemoryApi api, Func<string> cwd, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var words = new List<string>();
        var options = new Dictionary<string, object>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all":
                    options["all_repos"] = true;
                    break;
                case "--type" or "--since" or "--branch" when i + 1 < args.Length:
                    options[args[i][2..]] = args[++i];
                    break;
                case "--limit" or "-n" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n):
                    options["limit"] = n;
                    i++;
                    break;
                case var a when a.StartsWith("--", StringComparison.Ordinal):
                    throw new ArgumentException($"search: unknown option '{a}'");
                default:
                    words.Add(args[i]);
                    break;
            }
        }

        if (words.Count == 0)
        {
            throw new ArgumentException("search: give some words to look for, e.g. engram search webhook retry");
        }

        options["query"] = string.Join(' ', words);
        return Call("search", options, api, cwd, output);
    }

    public static int Show(string[] args, IMemoryApi api, Func<string> cwd, TextWriter output)
    {
        var ids = args.Select(a => long.TryParse(a.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : throw new ArgumentException($"show: '{a}' is not an observation id")).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("show: give one or more observation ids, e.g. engram show 12 15");
        }

        return Call("get_observations", new Dictionary<string, object> { ["ids"] = ids }, api, cwd, output);
    }

    private static int Call(string tool, Dictionary<string, object> arguments, IMemoryApi api, Func<string> cwd, TextWriter output)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                switch (value)
                {
                    case bool b: w.WriteBoolean(name, b); break;
                    case int n: w.WriteNumber(name, n); break;
                    case List<long> list:
                        w.WriteStartArray(name);
                        list.ForEach(w.WriteNumberValue);
                        w.WriteEndArray();
                        break;
                    default: w.WriteString(name, value.ToString()); break;
                }
            }

            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        var result = EngramTools.Create(api, cwd).First(t => t.Name == tool).Handle(doc.RootElement);
        output.WriteLine(result.Text.TrimEnd());
        return result.IsError ? 1 : 0;
    }
}
