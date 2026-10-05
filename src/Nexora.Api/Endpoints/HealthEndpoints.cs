using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Nexora.Api.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync
        });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteResponseAsync
        });
        return endpoints;
    }

    private static Task WriteResponseAsync(HttpContext context,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() },
            cancellationToken: context.RequestAborted);
    }
}
