using System.Numerics;
using System.Runtime.InteropServices;
using Dapper;
using Engram.Worker.Storage;

namespace Engram.Worker.Embedding;

internal static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var norm = MathF.Sqrt(Dot(v, v));
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }

        return v;
    }

    /// <summary>SIMD dot product; equals cosine similarity for normalized vectors.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
        {
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        var result = Vector.Dot(sum, Vector<float>.One);
        for (; i < n; i++)
        {
            result += a[i] * b[i];
        }

        return result;
    }

    public static byte[] ToBytes(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] b) => MemoryMarshal.Cast<byte, float>(b).ToArray();
}

internal sealed record VectorFilter(long? ProjectId, string? Type, string? Since, string? Branch);

/// <summary>
/// All vectors of the current model, in memory: brute-force cosine over a few thousand rows takes a few
/// milliseconds and needs no native extension (sqlite-vec is still prerelease). ~3 KB per observation.
/// </summary>
internal sealed class VectorIndex(Database database)
{
    private sealed record Entry(long Id, long? ProjectId, string Type, string? Branch, string CreatedAt, float[] Vector);

    private readonly Lock _gate = new();
    private Dictionary<long, Entry>? _entries;
    private string? _model;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries?.Count ?? 0;
            }
        }
    }

    /// <summary>Loads every vector stored for <paramref name="model"/> (once, or again when the model changes).</summary>
    public void EnsureLoaded(string model)
    {
        lock (_gate)
        {
            if (_entries is not null && _model == model)
            {
                return;
            }
        }

        using var c = database.Open();
        var rows = c.Query<(long Id, long? ProjectId, string Type, string? Branch, string CreatedAt, byte[] Vector)>(
            """
            SELECT o.id, o.project_id, o.type, o.branch, o.created_at, v.vector
            FROM observation_vectors v JOIN observations o ON o.id = v.observation_id
            WHERE v.model = @model
            """,
            new { model });
        var entries = rows.ToDictionary(r => r.Id, r => new Entry(r.Id, r.ProjectId, r.Type, r.Branch, r.CreatedAt, VectorMath.FromBytes(r.Vector)));
        lock (_gate)
        {
            _entries = entries;
            _model = model;
        }
    }

    public void Upsert(long id, long? projectId, string type, string? branch, string createdAt, float[] vector)
    {
        lock (_gate)
        {
            _entries ??= [];
            _entries[id] = new Entry(id, projectId, type, branch, createdAt, vector);
        }
    }

    public void Remove(long id)
    {
        lock (_gate)
        {
            _entries?.Remove(id);
        }
    }

    /// <summary>Nearest observations to <paramref name="query"/> that pass the filter, best first.</summary>
    public IReadOnlyList<(long Id, float Similarity)> Search(float[] query, VectorFilter filter, int k, double minSimilarity)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = _entries is null ? [] : [.. _entries.Values];
        }

        return snapshot
            .Where(e => (filter.ProjectId is null || e.ProjectId == filter.ProjectId)
                        && (filter.Type is null || e.Type == filter.Type)
                        && (filter.Since is null || string.CompareOrdinal(e.CreatedAt, filter.Since) >= 0)
                        && (filter.Branch is null || e.Branch == filter.Branch))
            .Select(e => (e.Id, Similarity: VectorMath.Dot(query, e.Vector)))
            .Where(x => x.Similarity >= minSimilarity)
            .OrderByDescending(x => x.Similarity)
            .Take(k)
            .ToList();
    }
}
