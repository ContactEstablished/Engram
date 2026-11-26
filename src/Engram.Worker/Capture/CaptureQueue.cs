using System.Threading.Channels;

namespace Engram.Worker.Capture;

/// <summary>A hook body as received; parsed and filtered later by <see cref="CaptureWriter"/>.</summary>
internal sealed record IngestItem(byte[] Body, string Origin);

/// <summary>
/// Hands hook bodies from the HTTP endpoint to the single writer, so a hook request never waits for git,
/// regexes or SQLite. Bounded: if the writer falls far behind, new items are dropped (fail open).
/// </summary>
internal sealed class CaptureQueue
{
    public const int Capacity = 10_000;

    private readonly Channel<IngestItem> _channel = Channel.CreateBounded<IngestItem>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private long _pending;
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(IngestItem item)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(item))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public ChannelReader<IngestItem> Reader => _channel.Reader;

    internal void MarkDone() => Interlocked.Decrement(ref _pending);

    /// <summary>Waits until everything enqueued so far has been processed. For tests and shutdown.</summary>
    public async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (Interlocked.Read(ref _pending) > 0)
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }
}
