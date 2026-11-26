using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Engram.Core;
using Engram.Core.Hooks;

namespace Engram.Cli.Spike;

/// <summary>Everything <see cref="SpikeCapture"/> touches outside itself, injectable for tests.</summary>
internal sealed record CaptureContext(
    Stream Stdin,
    Stream Stdout,
    Stream Stderr,
    Func<string, string?> GetEnv,
    long StartTimestamp,
    Func<double?> ProcessUptimeMs);

/// <summary>
/// <c>engram spike capture</c>: appends the hook JSON from stdin to <c>~/.engram/spike/&lt;event&gt;.jsonl</c>.
/// Hook rules: exit 0 always, never write to stdout/stderr, swallow every error into errors.log.
/// </summary>
internal static class SpikeCapture
{
    public const string DisableVariable = "ENGRAM_DISABLE";
    internal const int MaxInputBytes = 32 * 1024 * 1024;
    private const int ErrorSnippetChars = 500;

    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        // Keep non-ASCII and <>& readable in the capture files. Values are unchanged.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public static int Run(CaptureContext ctx)
    {
        // Recursion guard: Engram's own summarizer runs set this. Do nothing at all.
        if (ctx.GetEnv(DisableVariable) == "1")
        {
            return 0;
        }

        string? spikeDir = null;
        byte[]? input = null;
        try
        {
            spikeDir = EngramPaths.SpikeDir(ctx.GetEnv);
            input = ReadAll(ctx.Stdin, MaxInputBytes, out var truncated);

            if (truncated)
            {
                SafeFile.TryLogError(spikeDir, $"capture: stdin exceeded {MaxInputBytes} bytes; payload dropped.");
                return 0;
            }

            if (input.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
            {
                SafeFile.TryLogError(spikeDir, "capture: empty stdin.");
                return 0;
            }

            using var doc = JsonDocument.Parse(input);
            var eventName = HookPayloadReader.PeekEventName(input);
            var line = BuildLine(doc.RootElement, input.Length, ctx);

            SafeFile.Append(Path.Combine(spikeDir, FileNameFor(eventName) + ".jsonl"), line);
        }
        catch (Exception ex)
        {
            if (spikeDir is not null)
            {
                SafeFile.TryLogError(spikeDir, $"capture: {ex.GetType().Name}: {ex.Message} | input: {Snippet(input)}");
            }
        }

        return 0;
    }

    /// <summary>Maps an event name to a safe file name; anything odd becomes "unknown".</summary>
    internal static string FileNameFor(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return "unknown";
        }

        var chars = eventName.Length > 64 ? eventName[..64] : eventName;
        var builder = new StringBuilder(chars.Length);
        foreach (var c in chars)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.ToString();
    }

    private static byte[] BuildLine(JsonElement payload, int inputBytes, CaptureContext ctx)
    {
        var buffer = new ArrayBufferWriter<byte>(inputBytes + 256);
        using (var writer = new Utf8JsonWriter(buffer, s_writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("received_at", DateTimeOffset.Now);
            writer.WriteNumber("handler_ms", Math.Round(Stopwatch.GetElapsedTime(ctx.StartTimestamp).TotalMilliseconds, 3));
            if (ctx.ProcessUptimeMs() is { } uptime)
            {
                writer.WriteNumber("process_ms", Math.Round(uptime, 3));
            }

            writer.WriteString("os", OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription);
            writer.WriteBoolean("is_wsl", IsWsl(ctx.GetEnv));
            writer.WriteString("engram_version", typeof(SpikeCapture).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            writer.WriteNumber("stdin_bytes", inputBytes);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer); // re-serialized compactly so the record stays on one line
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsWsl(Func<string, string?> getEnv) =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(getEnv("WSL_DISTRO_NAME"));

    private static byte[] ReadAll(Stream stream, int maxBytes, out bool truncated)
    {
        using var memory = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (memory.Length + read > maxBytes)
                {
                    truncated = true;
                    return [];
                }

                memory.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        truncated = false;
        return memory.ToArray();
    }

    private static string Snippet(byte[]? input)
    {
        if (input is null || input.Length == 0)
        {
            return "<none>";
        }

        var text = Encoding.UTF8.GetString(input, 0, Math.Min(input.Length, ErrorSnippetChars * 4));
        return text.Length > ErrorSnippetChars ? text[..ErrorSnippetChars] + "…" : text;
    }
}
