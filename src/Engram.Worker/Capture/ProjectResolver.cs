using System.Collections.Concurrent;
using Engram.Core.Projects;

namespace Engram.Worker.Capture;

/// <summary>
/// <see cref="ProjectIdentity"/> with a short cache: hooks fire many times a minute from the same folder, and each
/// resolution runs git twice.
/// </summary>
internal sealed class ProjectResolver(TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (ProjectInfo Info, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    public ProjectInfo Resolve(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(cwd, out var hit) && now - hit.At < CacheFor)
        {
            return hit.Info;
        }

        var info = ProjectIdentity.Resolve(cwd);
        _cache[cwd] = (info, now);
        return info;
    }

    public static string? NormalizeRemote(string remote) => ProjectIdentity.NormalizeRemote(remote);

    public static string NormalizePath(string path) => ProjectIdentity.NormalizePath(path);
}
