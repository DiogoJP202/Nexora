using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Nexora.Api.Endpoints;
using Nexora.Application.Authentication;

namespace Nexora.Api.Security;

internal sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthenticationService authentication,
        IOptions<IdentityOptions> identity)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var stamp = context.User.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType);
            var valid = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                && userId != Guid.Empty
                && Guid.TryParse(context.User.FindFirstValue(AuthenticationConstants.SessionIdClaim), out var sessionId)
                && sessionId != Guid.Empty
                && Guid.TryParse(context.User.FindFirstValue(AuthenticationConstants.DeviceIdClaim), out var deviceId)
                && deviceId != Guid.Empty
                && !string.IsNullOrEmpty(stamp)
                && await authentication.ValidateSessionAsync(userId, sessionId, deviceId, stamp, context.RequestAborted);
            if (!valid)
            {
                // Stop before authorization: re-authentication could restore the cached bearer ticket.
                ApiProblems.NoStore(context);
                await ApiProblems.Result(StatusCodes.Status401Unauthorized, "authentication_required",
                    "A sessão não está disponível.").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
}
