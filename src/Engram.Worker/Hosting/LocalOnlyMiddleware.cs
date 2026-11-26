using System.Net;

namespace Engram.Worker;

/// <summary>
/// The worker is a local API with a key. Besides binding to 127.0.0.1, reject anything that looks like it
/// came through a browser: an <c>Origin</c> header, a non-loopback client, or a <c>Host</c> other than
/// localhost (DNS rebinding). Also records activity for idle shutdown.
/// </summary>
internal sealed class LocalOnlyMiddleware(RequestDelegate next, IdleTracker idle)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        idle.Touch();
        var request = context.Request;
        var remote = context.Connection.RemoteIpAddress;
        var host = request.Host.Host;
        var hostOk = host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        if (request.Headers.Origin.Count > 0 || !hostOk || (remote is not null && !IPAddress.IsLoopback(remote)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
