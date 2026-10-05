using Nexora.Application.Authentication;

namespace Nexora.Api.Endpoints;

internal static class DeviceEndpoints
{
    internal static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/devices").WithTags("Devices").RequireAuthorization();
        group.MapGet("", async (HttpContext context, IAuthenticationService authentication,
            CancellationToken cancellationToken) => Results.Ok(await authentication.ListDevicesAsync(
                AuthenticationEndpoints.UserId(context.User), cancellationToken)))
            .Produces<DeviceSummary[]>().ProducesProblem(401).ProducesProblem(503);
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context,
            IAuthenticationService authentication, CancellationToken cancellationToken) =>
            await authentication.RevokeDeviceAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken)
                ? Results.NoContent()
                : ApiProblems.Result(StatusCodes.Status404NotFound, "resource_not_found", "Recurso não encontrado."))
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        return endpoints;
    }
}
