using Microsoft.AspNetCore.Diagnostics;
using Nexora.Api.Endpoints;
using Npgsql;

namespace Nexora.Api.Security;

internal sealed class SanitizedExceptionHandler(ILogger<SanitizedExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
            return true;
        }
        var status = exception switch
        {
            BadHttpRequestException badRequest => badRequest.StatusCode,
            _ when IsDatabaseFailure(exception) => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        // Provider messages can contain credentials or personal data; never log the exception object.
        logger.LogWarning(new EventId(2201, "RequestFailed"),
            "Request failed. Type {ExceptionType}, status {Status}, trace {TraceId}",
            exception.GetType().Name, status, context.TraceIdentifier);
        ApiProblems.NoStore(context);
        await ApiProblems.Result(status, status switch
        {
            400 => "invalid_request",
            413 => "request_too_large",
            503 => "service_unavailable",
            _ => "internal_error"
        }, "Não foi possível atender a requisição.").ExecuteAsync(context);
        return true;
    }

    private static bool IsDatabaseFailure(Exception exception)
    {
        // EF can wrap provider failures, without changing the public availability result.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or TimeoutException)
            {
                return true;
            }
        }
        return false;
    }
}
