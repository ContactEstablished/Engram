namespace Engram.Worker;

/// <summary>Last request time, for idle shutdown.</summary>
internal sealed class IdleTracker(TimeProvider time)
{
    private long _lastTicks = time.GetUtcNow().UtcTicks;

    public void Touch() => Interlocked.Exchange(ref _lastTicks, time.GetUtcNow().UtcTicks);

    public TimeSpan IdleFor => time.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);
}

/// <summary>Stops the worker after <c>worker.idleShutdownMinutes</c> without requests (0 = never).</summary>
internal sealed partial class IdleShutdownService(IdleTracker idle, WorkerContext context, Engram.Worker.Observer.ObserverStore jobs, Engram.Worker.Observer.ObserverRuntime runtime, IHostApplicationLifetime lifetime, ILogger<IdleShutdownService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = context.Settings.Worker.IdleShutdownMinutes;
        if (minutes <= 0)
        {
            return;
        }

        var limit = TimeSpan.FromMinutes(minutes);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Queued summaries still need the worker; it stops once they are done (or parked).
            if (idle.IdleFor >= limit && (runtime.Observer is null || jobs.OpenJobCount() == 0))
            {
                LogIdle(minutes);
                lifetime.StopApplication();
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No requests for {Minutes} minutes; shutting down")]
    private partial void LogIdle(int minutes);
}
