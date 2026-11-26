using Engram.Worker.Memory;

namespace Engram.Worker.Embedding;

internal enum SearchMode
{
    Hybrid,
    Keyword,
    Semantic,
}

internal sealed record SearchOutcome(IReadOnlyList<ObservationRow> Rows, SearchMode Used, string? Note);

/// <summary>
/// Keyword (FTS5 bm25) and meaning (cosine over embeddings) searches, merged with reciprocal-rank fusion so
/// neither score scale dominates. Falls back to keyword-only when embeddings are off or the service is down.
/// </summary>
internal sealed class HybridSearcher(MemoryStore store, VectorIndex index, IEmbeddingClient client, EmbeddingService embeddings, WorkerContext context)
{
    private const int CandidatePool = 50;
    private const double RrfK = 60;

    public async Task<SearchOutcome> SearchAsync(SearchQuery query, SearchMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var settings = context.Settings.Search;
        var wantVectors = mode != SearchMode.Keyword && settings.Semantic && !string.IsNullOrWhiteSpace(query.Text);
        var keyword = mode == SearchMode.Semantic && wantVectors ? [] : store.Search(query with { Limit = CandidatePool });
        if (!wantVectors)
        {
            return new SearchOutcome([.. keyword.Take(query.Limit)], SearchMode.Keyword, null);
        }

        float[] vector;
        try
        {
            vector = (await client.EmbedAsync([settings.QueryPrefix + query.Text], TimeSpan.FromMilliseconds(settings.QueryTimeoutMs), cancellationToken).ConfigureAwait(false))[0];
        }
        catch (EmbeddingException ex)
        {
            embeddings.RequestWarmUp();
            var fallback = mode == SearchMode.Semantic ? store.Search(query with { Limit = CandidatePool }) : keyword;
            return new SearchOutcome([.. fallback.Take(query.Limit)], SearchMode.Keyword, "keyword only: " + ex.Message);
        }

        index.EnsureLoaded(client.Model);
        var nearest = index.Search(vector, new VectorFilter(query.ProjectId, query.Type, query.Since, query.Branch), CandidatePool, settings.MinSimilarity);
        if (mode == SearchMode.Semantic)
        {
            return new SearchOutcome(Ordered(nearest.Select(n => n.Id).Take(query.Limit).ToList()), SearchMode.Semantic, null);
        }

        // Reciprocal-rank fusion: score = sum over lists of 1 / (k + rank).
        var scores = new Dictionary<long, double>();
        for (var i = 0; i < keyword.Count; i++)
        {
            scores[keyword[i].Id] = 1 / (RrfK + i + 1);
        }

        for (var i = 0; i < nearest.Count; i++)
        {
            scores[nearest[i].Id] = scores.GetValueOrDefault(nearest[i].Id) + (1 / (RrfK + i + 1));
        }

        var top = scores.OrderByDescending(s => s.Value).ThenByDescending(s => s.Key).Take(query.Limit).Select(s => s.Key).ToList();
        return new SearchOutcome(Ordered(top), SearchMode.Hybrid, null);
    }

    /// <summary>Rows for <paramref name="ids"/>, in that order.</summary>
    private List<ObservationRow> Ordered(List<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = store.Get(ids).ToDictionary(r => r.Id);
        return [.. ids.Where(rows.ContainsKey).Select(id => rows[id])];
    }
}
