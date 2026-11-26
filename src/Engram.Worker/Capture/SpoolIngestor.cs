using Engram.Core;

namespace Engram.Worker.Capture;

/// <summary>
/// Picks up hook payloads that <c>engram hook session-start</c> wrote to <c>~/.engram/spool</c> while the
/// worker was down or still starting. Checks at startup and then every few seconds.
/// </summary>
internal sealed partial class SpoolIngestor(CaptureQueue queue, WorkerContext context, ILogger<SpoolIngestor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            IngestOnce();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Enqueues and deletes every spooled file, oldest first. Returns how many were queued.</summary>
    public int IngestOnce()
    {
        var dir = EngramPaths.Spool(context.Home);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var body = File.ReadAllBytes(file);
                File.Delete(file);
                if (queue.TryEnqueue(new IngestItem(body, "spool")))
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being written, or locked; next tick.
                LogSkipped(Path.GetFileName(file), ex.Message);
            }
        }

        return count;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Spool file {File} skipped for now: {Reason}")]
    private partial void LogSkipped(string file, string reason);
}
