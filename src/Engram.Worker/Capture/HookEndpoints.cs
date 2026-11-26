using Engram.Core.Security;

namespace Engram.Worker.Capture;

/// <summary>
/// <c>POST /hooks/{event}</c>: authenticate, honor the recursion guard, queue, answer. Always an empty
/// 200 for anything Engram chooses not to store (a 2xx with a text body is a hook error in Claude Code).
/// </summary>
internal static partial class HookEndpoints
{
    public const int MaxBodyBytes = 8 * 1024 * 1024;
    public const string DisableHeader = "X-Engram-Disable";

    public static void MapHookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/hooks/{name?}", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpContext http, CaptureQueue queue, WorkerContext context, ILoggerFactory loggers)
    {
        if (!WorkerToken.Matches(http.Request.Headers.Authorization, context.Token))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Recursion guard: events from Engram's own summarizer runs carry ENGRAM_DISABLE=1.
        if (http.Request.Headers[DisableHeader] == "1")
        {
            return Results.Ok();
        }

        if (http.Request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
        if (buffer.Length == 0 || buffer.Length > MaxBodyBytes)
        {
            return Results.Ok();
        }

        if (!queue.TryEnqueue(new IngestItem(buffer.ToArray(), "http")))
        {
            LogQueueFull(loggers.CreateLogger(typeof(HookEndpoints)), queue.Dropped);
        }

        return Results.Ok();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture queue full; dropped a hook event ({Dropped} dropped so far)")]
    private static partial void LogQueueFull(ILogger logger, long dropped);
}
