using Microsoft.AspNetCore.SignalR;

namespace SqlAgMonitor.Service.Hubs;

/// <summary>
/// Global SignalR hub filter that catches unhandled exceptions in hub method
/// invocations, logs them server-side, and returns a generic error message
/// to the client. Prevents internal details (file paths, stack traces,
/// connection strings) from leaking to connected clients.
/// </summary>
internal sealed class HubExceptionFilter : IHubFilter
{
    private readonly ILogger<HubExceptionFilter> _logger;

    public HubExceptionFilter(ILogger<HubExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (HubException)
        {
            // Already a client-safe exception; re-throw as-is.
            throw;
        }
        catch (OperationCanceledException) when (invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            // Client disconnected mid-invocation; not an error.
            _logger.LogDebug(
                "Hub method {Method} cancelled (client {ConnectionId} disconnected).",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception in hub method {Method} for client {ConnectionId}.",
                invocationContext.HubMethodName,
                invocationContext.Context.ConnectionId);

            throw new HubException("An internal error occurred. Please try again or contact the administrator.");
        }
    }
}
