using System.Security.Claims;
using Nexora.Application.Authentication;

namespace Nexora.Api.Endpoints;

internal static class AuthenticationEndpoints
{
    internal static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Authentication");
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("login")
            .Produces<AuthenticationTokens>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(429).ProducesProblem(503);
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("refresh")
            .Produces<AuthenticationTokens>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(429).ProducesProblem(503);
        group.MapPost("/logout", async (HttpContext context, IAuthenticationService authentication,
            CancellationToken cancellationToken) =>
        {
            await authentication.LogoutAsync(UserId(context.User),
                Guid.Parse(context.User.FindFirstValue(AuthenticationConstants.SessionIdClaim)!), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization().Produces(StatusCodes.Status204NoContent).ProducesProblem(401).ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request,
        IAuthenticationService authentication, CancellationToken cancellationToken)
    {
        if (!ValidText(request.Login, 254) || request.Password is not { Length: > 0 and <= 256 }
            || !ValidText(request.DeviceName, 100) || !ValidText(request.Platform, 32)
            || request.DeviceId == Guid.Empty)
        {
            return ApiProblems.InvalidRequest();
        }
        var result = await authentication.LoginAsync(new LoginCommand(request.Login!.Trim(), request.Password!,
            request.DeviceName!.Trim(), request.Platform!.Trim(), request.DeviceId), cancellationToken);
        return result.Failure switch
        {
            null => Results.Ok(result.Value),
            AuthenticationFailure.InvalidDevice => ApiProblems.Result(StatusCodes.Status400BadRequest,
                "invalid_device", "O dispositivo informado não está disponível."),
            _ => ApiProblems.Result(StatusCodes.Status401Unauthorized, "invalid_credentials", "Credenciais inválidas.")
        };
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request,
        IAuthenticationService authentication, CancellationToken cancellationToken)
    {
        if (request.RefreshToken is not { Length: > 0 and <= 128 })
        {
            return ApiProblems.InvalidRequest();
        }
        var result = await authentication.RefreshAsync(request.RefreshToken, cancellationToken);
        return result.Succeeded ? Results.Ok(result.Value)
            : ApiProblems.Result(StatusCodes.Status401Unauthorized, "invalid_refresh_token", "Refresh token inválido.");
    }

    internal static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    // These objects must never be included in logs.
    internal sealed class LoginRequest
    {
        public string? Login { get; init; }
        public string? Password { get; init; }
        public string? DeviceName { get; init; }
        public string? Platform { get; init; }
        public Guid? DeviceId { get; init; }
    }

    internal sealed class RefreshRequest
    {
        public string? RefreshToken { get; init; }
    }
}
