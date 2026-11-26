using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Engram.Worker.Observer;
using Engram.Worker.Storage;

namespace Engram.Worker.Embedding;

/// <summary>Wakes the embedding service when new observations are stored.</summary>
internal sealed class EmbeddingSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _semaphore.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Keeps every observation embedded: warms the model at start, then embeds whatever has no vector for the
/// current model (new observations, saved memories, or everything after a model change). When the embedding
/// service is unreachable it backs off and tries again; search is keyword-only meanwhile.
/// </summary>
internal sealed partial class EmbeddingService(
    Database database,
    IEmbeddingClient client,
    VectorIndex index,
    EmbeddingSignal signal,
    WorkerContext context,
    TimeProvider time,
    ILogger<EmbeddingService> logger) : BackgroundService
{
    public const int BatchSize = 32;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(90);

    private volatile bool _warmed;

    /// <summary>Asks for the model to be loaded again (a search found it cold, or a session is starting).</summary>
    public void RequestWarmUp()
    {
        _warmed = false;
        signal.Notify();
    }

    /// <summary>Last error talking to the embedding service, or null when the last call worked (shown by /stats).</summary>
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!context.Settings.Search.Semantic)
        {
            return;
        }

        index.EnsureLoaded(client.Model);
        var reported = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_warmed)
                {
                    // Loads the model into memory (tens of seconds when cold) so searches answer in milliseconds.
                    await client.EmbedAsync([context.Settings.Search.QueryPrefix + "warm up"], BatchTimeout, stoppingToken).ConfigureAwait(false);
                    _warmed = true;
                    LastError = null;
                }

                var embedded = await EmbedPendingAsync(stoppingToken).ConfigureAwait(false);
                if (reported)
                {
                    LogRecovered(client.Model);
                    reported = false;
                }

                if (embedded == 0)
                {
                    await signal.WaitAsync(Poll, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (EmbeddingException ex)
            {
                LastError = ex.Message;
                if (!reported)
                {
                    LogUnavailable(ex.Message);
                    reported = true;
                }

                await Task.Delay(Backoff, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogError(ex);
                await Task.Delay(Backoff, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Embeds up to <see cref="BatchSize"/> observations that have no vector for the current model.</summary>
    internal async Task<int> EmbedPendingAsync(CancellationToken cancellationToken)
    {
        List<(long Id, long? ProjectId, string Type, string Title, string Narrative, string Facts, string Files, string? Branch, string CreatedAt)> pending;
        using (var c = database.Open())
        {
            pending = [.. c.Query<(long, long?, string, string, string, string, string, string?, string)>(
                """
                SELECT o.id, o.project_id, o.type, o.title, o.narrative, o.facts, o.files, o.branch, o.created_at
                FROM observations o
                LEFT JOIN observation_vectors v ON v.observation_id = o.id AND v.model = @model
                WHERE v.observation_id IS NULL
                ORDER BY o.id DESC
                LIMIT @limit
                """,
                new { model = client.Model, limit = BatchSize })];
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        var prefix = context.Settings.Search.DocumentPrefix;
        var vectors = await client.EmbedAsync([.. pending.Select(p => prefix + DocumentText(p.Title, p.Narrative, p.Facts, p.Files))], BatchTimeout, cancellationToken).ConfigureAwait(false);
        LastError = null;
        var now = ObserverStore.Iso(time.GetUtcNow());
        using (var c = database.Open())
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < pending.Count; i++)
            {
                c.Execute(
                    """
                    INSERT INTO observation_vectors (observation_id, model, dims, vector, created_at)
                    SELECT @id, @model, @dims, @vector, @now WHERE EXISTS (SELECT 1 FROM observations WHERE id = @id)
                    ON CONFLICT(observation_id) DO UPDATE SET model = excluded.model, dims = excluded.dims, vector = excluded.vector, created_at = excluded.created_at
                    """,
                    new { id = pending[i].Id, model = client.Model, dims = vectors[i].Length, vector = VectorMath.ToBytes(vectors[i]), now },
                    tx);
            }

            tx.Commit();
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            index.Upsert(p.Id, p.ProjectId, p.Type, p.Branch, p.CreatedAt, vectors[i]);
        }

        LogEmbedded(pending.Count, client.Model);
        return pending.Count;
    }

    /// <summary>What gets embedded: title, narrative, facts and file names. Not the raw tool output.</summary>
    internal static string DocumentText(string title, string narrative, string factsJson, string filesJson)
    {
        var sb = new StringBuilder(title.Trim().TrimEnd('.')).Append(". ").Append(narrative.Trim());
        var facts = Parse(factsJson);
        if (facts.Count > 0)
        {
            sb.Append(' ').Append(string.Join(". ", facts)).Append('.');
        }

        var files = Parse(filesJson);
        if (files.Count > 0)
        {
            sb.Append(" Files: ").Append(string.Join(", ", files));
        }

        var text = sb.ToString();
        return text.Length <= 4000 ? text : text[..4000];
    }

    private static List<string> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedded {Count} observation(s) with {Model}")]
    private partial void LogEmbedded(int count, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding service unavailable; search is keyword-only until it returns: {Error}")]
    private partial void LogUnavailable(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding service back ({Model})")]
    private partial void LogRecovered(string model);

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding service error")]
    private partial void LogError(Exception ex);

    internal static string Describe(int vectors, long observations, string? lastError) =>
        string.Create(CultureInfo.InvariantCulture, $"{vectors}/{observations}{(lastError is null ? string.Empty : " (" + lastError + ")")}");
}
