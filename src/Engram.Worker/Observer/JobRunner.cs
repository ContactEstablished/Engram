using Engram.Core.Settings;
using Engram.Worker.Storage;

namespace Engram.Worker.Observer;

/// <summary>Wakes the runner as soon as capture queues work, instead of waiting for the next poll.</summary>
internal sealed class JobSignal
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
/// Runs the job queue off the hook path, one job at a time: turn observations, checkpoint and final session
/// summaries. Failed jobs back off and are parked after three attempts; a call budget per hour caps cost.
/// </summary>
internal sealed partial class JobRunner(
    ObserverStore store,
    ObserverRuntime runtime,
    WorkerContext context,
    JobSignal signal,
    TimeProvider time,
    ILogger<JobRunner> logger,
    Engram.Worker.Embedding.EmbeddingSignal? embeddingSignal = null) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BudgetWait = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var observer = runtime.Observer;
        var client = runtime.Client;
        if (observer is null || client is null)
        {
            LogDisabled(context.Settings.Observer.Provider);
            return;
        }

        var reset = store.ResetRunningJobs();
        LogStarted(client.Provider, client.Model, reset);
        var budgetLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store.CallsInLastHour() >= context.Settings.Observer.MaxCallsPerHour)
                {
                    if (!budgetLogged)
                    {
                        LogBudget(context.Settings.Observer.MaxCallsPerHour);
                        budgetLogged = true;
                    }

                    await Task.Delay(BudgetWait, time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                budgetLogged = false;
                var job = store.ClaimNextJob();
                if (job is null)
                {
                    await signal.WaitAsync(IdlePoll, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunJobAsync(job, observer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The runner itself must survive anything (a locked database, a bad row).
                LogRunnerError(ex);
                await Task.Delay(IdlePoll, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs one claimed job to completion, retry or parking. Public to the tests.</summary>
    internal async Task RunJobAsync(JobRow job, Observer observer, CancellationToken cancellationToken)
    {
        try
        {
            switch (job.Kind)
            {
                case JobKinds.ObserveTurn:
                    var turn = store.LoadTurn(job);
                    var observed = await observer.ObserveTurnAsync(turn, job.Id, cancellationToken).ConfigureAwait(false);
                    var saved = store.SaveObservations(job, observed.Observations, observed.Provider, observed.Model, [.. turn.Events.Select(e => e.Id)]);
                    if (saved > 0)
                    {
                        embeddingSignal?.Notify();
                    }

                    LogObserved(job.Id, saved, turn.Events.Count);
                    break;
                case JobKinds.SummarizeCheckpoint or JobKinds.SummarizeSession:
                    var session = store.LoadSession(job.SessionId);
                    var summary = await observer.SummarizeAsync(session, job.Id, cancellationToken).ConfigureAwait(false);
                    store.SaveSummary(job, job.Kind == JobKinds.SummarizeSession ? "final" : "checkpoint", summary.Summary, summary.Provider, summary.Model);
                    LogSummarized(job.Id, job.Kind);
                    break;
                case JobKinds.SummarizeNote:
                    var note = store.LoadNote(job) ?? throw new NothingToObserveException("the note was deleted before it was summarized");
                    var condensed = await observer.SummarizeNoteAsync(note, job.Id, cancellationToken).ConfigureAwait(false);
                    store.SaveNoteSummary(job, note, condensed.Summary, condensed.Provider, condensed.Model);
                    embeddingSignal?.Notify();
                    LogSummarized(job.Id, job.Kind);
                    break;
                default:
                    store.Complete(job, $"unknown job kind '{job.Kind}'");
                    break;
            }
        }
        catch (NothingToObserveException ex)
        {
            store.Complete(job, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutting down: the job stays 'running' and is reset at next start
        }
        catch (Exception ex)
        {
            var outcome = store.Fail(job, ex.Message);
            LogJobFailed(job.Id, job.Kind, outcome, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Observer disabled (observer.provider = {Provider}); jobs stay queued")]
    private partial void LogDisabled(string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Observer started: {Provider}/{Model}; {Reset} interrupted job(s) re-queued")]
    private partial void LogStarted(string provider, string model, int reset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Observer paused: {Max} provider calls in the last hour (observer.maxCallsPerHour)")]
    private partial void LogBudget(int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Id}: {Observations} observation(s) from {Events} tool event(s)")]
    private partial void LogObserved(long id, int observations, int events);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Id}: {Kind} saved")]
    private partial void LogSummarized(long id, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Id} ({Kind}) failed -> {Outcome}: {Error}")]
    private partial void LogJobFailed(long id, string kind, string outcome, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job runner error")]
    private partial void LogRunnerError(Exception ex);
}

/// <summary>The configured provider and the observer built on it; both null when <c>observer.provider</c> is <c>none</c>.</summary>
internal sealed record ObserverRuntime(Observer? Observer, ILlmClient? Client)
{
    public static ObserverRuntime Create(WorkerContext context, ObserverStore store, IProcessRunner runner, HttpClient http, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings.Observer;
        ILlmClient? client = settings.Provider switch
        {
            ObserverSettings.ClaudeCli => new ClaudeCliClient(settings, context.Home, runner, getEnv),
            ObserverSettings.Ollama => new OllamaClient(settings, http),
            _ => null,
        };
        var redactor = new Engram.Core.Security.Redactor(context.Settings.Capture.RedactPatterns);
        return new ObserverRuntime(client is null ? null : new Observer(client, store, settings, redactor), client);
    }
}
