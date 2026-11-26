using System.Text.Json;
using Engram.Core.Hooks;

namespace Engram.Worker.Capture;

/// <summary>The only database writer for capture. Processes queued hook bodies one at a time.</summary>
internal sealed partial class CaptureWriter(CaptureQueue queue, CaptureProcessor processor, Engram.Worker.Observer.JobSignal jobSignal, ILogger<CaptureWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var payload = HookPayloadReader.Read(item.Body);
                var outcome = processor.Process(payload);
                LogProcessed(payload.HookEventName, outcome, item.Origin);
                if (outcome == CaptureOutcome.Stored)
                {
                    jobSignal.Notify();
                }
            }
            catch (JsonException ex)
            {
                LogInvalidPayload(item.Origin, ex.Message);
            }
            catch (Exception ex)
            {
                // One bad event must never stop capture.
                LogFailed(ex, item.Origin);
            }
            finally
            {
                queue.MarkDone();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Event} -> {Outcome} ({Origin})")]
    private partial void LogProcessed(string @event, CaptureOutcome outcome, string origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid hook payload from {Origin}: {Message}")]
    private partial void LogInvalidPayload(string origin, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process hook payload from {Origin}")]
    private partial void LogFailed(Exception ex, string origin);
}
