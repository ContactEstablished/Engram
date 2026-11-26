using System.Text.Json;
using Engram.Core.Settings;

namespace Engram.Worker.Embedding;

/// <summary>Turns text into vectors. Swappable for tests.</summary>
internal interface IEmbeddingClient
{
    string Model { get; }

    /// <exception cref="EmbeddingException">The service is down, slow, or answered badly.</exception>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class EmbeddingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Ollama's <c>/api/embed</c>, batched. Local only by default. <c>keep_alive</c> keeps the model loaded, because a
/// cold load takes tens of seconds and would push every search into the keyword-only fallback.
/// </summary>
internal sealed class OllamaEmbeddingClient(SearchSettings settings, HttpClient http) : IEmbeddingClient
{
    public string Model => settings.EmbeddingModel;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.EmbeddingModel,
                ["input"] = texts,
                ["keep_alive"] = settings.KeepAlive,
                ["truncate"] = true,
            };
            var url = new Uri(new Uri(settings.EmbeddingUrl.TrimEnd('/') + "/"), "api/embed");
            using var response = await http.PostAsJsonAsync(url, body, cts.Token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new EmbeddingException($"ollama answered HTTP {(int)response.StatusCode}: {(json.Length > 200 ? json[..200] : json)}");
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings) || embeddings.GetArrayLength() != texts.Count)
            {
                throw new EmbeddingException("ollama returned no embeddings (is the model pulled? `ollama pull " + settings.EmbeddingModel + "`)");
            }

            return [.. embeddings.EnumerateArray().Select(e => VectorMath.Normalize([.. e.EnumerateArray().Select(v => v.GetSingle())]))];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingException($"ollama did not answer within {timeout.TotalMilliseconds:0} ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            throw new EmbeddingException("ollama call failed: " + ex.Message, ex);
        }
    }
}
