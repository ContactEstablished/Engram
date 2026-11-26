namespace Engram.Core;

/// <summary>The kinds of memory Engram stores. Shared by the worker, the observer's parser and <c>engram save --type</c>.</summary>
public static class ObservationTypes
{
    public static readonly IReadOnlyList<string> All = ["decision", "bugfix", "feature", "change", "discovery", "gotcha"];

    /// <summary>The canonical spelling of <paramref name="value"/>, or null when it isn't a known type.</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is not null && All.Contains(v) ? v : null;
    }
}
