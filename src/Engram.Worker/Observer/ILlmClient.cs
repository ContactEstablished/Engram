namespace Engram.Worker.Observer;

/// <summary>One text-in, text-out call to a summarizer. Providers differ only in how they run it.</summary>
internal interface ILlmClient
{
    string Provider { get; }

    string Model { get; }

    /// <exception cref="LlmException">The call failed; the job is retried.</exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
