using Engram.Core.Security;
using Engram.Core.Settings;

namespace Engram.Worker.Observer;

internal sealed record ObservedTurn(IReadOnlyList<Observation> Observations, string Provider, string Model);

internal sealed record ObservedSummary(SessionSummary Summary, string Provider, string Model);

/// <summary>
/// Turns a turn (or a session) into memory through a provider. Every provider call is logged. Invalid JSON
/// gets one repair retry; after that the job fails and the runner decides whether to retry later.
/// </summary>
internal sealed class Observer(ILlmClient client, ObserverStore store, ObserverSettings settings, Redactor redactor)
{
    public async Task<ObservedTurn> ObserveTurnAsync(TurnInput turn, long jobId, CancellationToken cancellationToken)
    {
        var user = TurnRenderer.RenderTurn(turn, settings.MaxInputChars, redactor);
        var (observations, model) = await CallAsync(Prompts.ObserveSystem, user, jobId, text => OutputParser.ParseObservations(text), cancellationToken).ConfigureAwait(false);
        return new ObservedTurn(observations, client.Provider, model);
    }

    public async Task<ObservedSummary> SummarizeAsync(SessionInput session, long jobId, CancellationToken cancellationToken)
    {
        var user = TurnRenderer.RenderSession(session, settings.MaxInputChars, redactor);
        var (summary, model) = await CallAsync(Prompts.SummarizeSystem, user, jobId, text => OutputParser.ParseSummary(text), cancellationToken).ConfigureAwait(false);
        return new ObservedSummary(summary, client.Provider, model);
    }

    private async Task<(T Value, string Model)> CallAsync<T>(string system, string user, long jobId, Func<string, (T? Value, string? Error)> parse, CancellationToken cancellationToken)
        where T : class
    {
        var response = await SendAsync(new LlmRequest(system, user), jobId, cancellationToken).ConfigureAwait(false);
        var (value, error) = parse(response.Text);
        if (value is not null)
        {
            return (value, response.Model);
        }

        // One repair attempt: show the model what was wrong with its own reply.
        response = await SendAsync(new LlmRequest(system, user + Prompts.RepairSuffix(error ?? "unknown problem", response.Text)), jobId, cancellationToken).ConfigureAwait(false);
        (value, error) = parse(response.Text);
        return value is not null
            ? (value, response.Model)
            : throw new LlmException($"the summarizer's reply was unusable after a repair attempt: {error}");
    }

    private async Task<LlmResponse> SendAsync(LlmRequest request, long jobId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            store.LogCall(jobId, client.Provider, response.Model, ok: true, error: null, response.Usage, response.Latency);
            return response;
        }
        catch (LlmException ex)
        {
            store.LogCall(jobId, client.Provider, client.Model, ok: false, ex.Message, usage: null, ex.Latency ?? TimeSpan.Zero);
            throw;
        }
    }
}
