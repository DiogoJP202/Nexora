using Nexora.Api.Endpoints;
using Nexora.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "NEXORA_");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["code"] = context.ProblemDetails.Status switch
        {
            StatusCodes.Status404NotFound => "resource_not_found",
            StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
            StatusCodes.Status500InternalServerError => "internal_error",
            _ => "request_failed"
        };
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Health is served over loopback in phase 1. HTTPS termination via Tailscale
// and authentication policies are added before the production deployment.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapHealthEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

public partial class Program;
