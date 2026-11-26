using System.Diagnostics;
using System.Text.Json;
using Engram.Core.Settings;

namespace Engram.Worker.Observer;

/// <summary>A local Ollama server: nothing leaves the machine. Free, so cost is recorded as 0.</summary>
internal sealed class OllamaClient(ObserverSettings settings, HttpClient http) : ILlmClient
{
    public string Provider => ObserverSettings.Ollama;

    public string Model => settings.OllamaOptions.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var body = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["stream"] = false,
                ["format"] = "json",
                ["think"] = false,
                ["options"] = new Dictionary<string, object?> { ["temperature"] = 0.2 },
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = request.System },
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = request.User },
                },
            };
            using var response = await http.PostAsJsonAsync(new Uri(new Uri(settings.OllamaOptions.Url.TrimEnd('/') + "/"), "api/chat"), body, cts.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(started);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException($"ollama answered HTTP {(int)response.StatusCode}: {(json.Length > 200 ? json[..200] : json)}", latency);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new LlmException("ollama returned an empty reply", latency);
            }

            long? Count(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
            return new LlmResponse(text, new LlmUsage(Count("prompt_eval_count"), Count("eval_count"), null, null, 0), latency, Model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"ollama timed out after {settings.TimeoutSeconds}s", Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new LlmException("ollama call failed: " + ex.Message, Stopwatch.GetElapsedTime(started), ex);
        }
    }
}
