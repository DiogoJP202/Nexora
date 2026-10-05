using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Administration;
using Nexora.Api.Endpoints;
using Nexora.Api.Security;
using Nexora.Application.Authentication;
using Nexora.Infrastructure;

var administrationCommand = args.FirstOrDefault() is "bootstrap-admin" or "reset-admin-password"
    ? args[0] : null;
if (administrationCommand is not null && args.Length != 1)
{
    Console.Error.WriteLine("O comando não aceita argumentos. Informe os dados no terminal interativo.");
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(administrationCommand is null ? args : []);
builder.Configuration.AddEnvironmentVariables(prefix: "NEXORA_");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
        {
            StatusCodes.Status400BadRequest => "invalid_request",
            StatusCodes.Status401Unauthorized => "authentication_required",
            StatusCodes.Status403Forbidden => "access_denied",
            StatusCodes.Status404NotFound => "resource_not_found",
            StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
            StatusCodes.Status413PayloadTooLarge => "request_too_large",
            StatusCodes.Status429TooManyRequests => "rate_limited",
            StatusCodes.Status503ServiceUnavailable => "service_unavailable",
            StatusCodes.Status500InternalServerError => "internal_error",
            _ => "request_failed"
        });
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddExceptionHandler<SanitizedExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddKeyProtection(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(AuthenticationConstants.Scheme)
        .RequireAuthenticatedUser().Build();
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        ApiProblems.NoStore(context.HttpContext);
        await ApiProblems.Result(StatusCodes.Status429TooManyRequests, "rate_limited",
            "Limite de requisições excedido.").ExecuteAsync(context.HttpContext);
    };
    AddPolicy("login", 5);
    AddPolicy("refresh", 30);

    void AddPolicy(string name, int limit) => options.AddPolicy(name, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

await using var app = builder.Build();
if (administrationCommand is not null)
{
    Environment.ExitCode = await AccountAdministrationCommand.RunAsync(app.Services, administrationCommand);
    return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        ApiProblems.NoStore(context);
        if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
        {
            await ApiProblems.Result(StatusCodes.Status400BadRequest, "https_required",
                "HTTPS é obrigatório.").ExecuteAsync(context);
            return;
        }
        if (context.Request.ContentLength > 16 * 1024)
        {
            await ApiProblems.Result(StatusCodes.Status413PayloadTooLarge, "request_too_large",
                "O corpo da requisição excede o limite.").ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<SessionValidationMiddleware>();
app.UseAuthorization();
app.MapHealthEndpoints();
app.MapAuthenticationEndpoints();
app.MapDeviceEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
app.MapFallback("/{**path}", () => Results.NotFound()).AllowAnonymous();
await app.RunAsync();

public partial class Program;
