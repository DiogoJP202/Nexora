using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using Nexora.Application.Authentication;
using Nexora.Application.Content;
using Nexora.Application.Uploads;
using Nexora.Domain.Content;
using Nexora.Domain.Uploads;

namespace Nexora.Api.Endpoints;

internal sealed record ChunkBodyLimitMetadata;

internal static class UploadEndpoints
{
    internal static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/uploads").WithTags("Uploads").RequireAuthorization();
        group.MapPost("", CreateAsync).Produces<UploadSnapshot>(StatusCodes.Status201Created)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(413).ProducesProblem(507).ProducesProblem(503);
        group.MapGet("/{id:guid}", async (Guid id, HttpContext context, IUploadService uploads, CancellationToken cancellationToken) =>
        {
            var upload = await uploads.GetAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken);
            return upload is null ? NotFound() : Results.Ok(upload);
        }).Produces<UploadSnapshot>().ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        group.MapPut("/{id:guid}/chunks/{number:int}", PutChunkAsync).WithMetadata(new ChunkBodyLimitMetadata())
            .Produces<UploadChunkReceipt>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(503);
        group.MapPost("/{id:guid}/complete", async (Guid id, HttpContext context, IUploadService uploads, CancellationToken cancellationToken) =>
        {
            var upload = await uploads.CompleteAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken);
            if (upload is null) return NotFound();
            return upload.State == UploadState.Finalizing ? Results.Accepted($"/api/uploads/{id}", upload) : Results.Ok(upload);
        }).Produces<UploadSnapshot>().Produces<UploadSnapshot>(StatusCodes.Status202Accepted)
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, IUploadService uploads, CancellationToken cancellationToken) =>
            await uploads.CancelAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken) ? Results.NoContent() : NotFound())
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(CreateUploadRequest request, HttpContext context,
        IUploadService uploads, CancellationToken cancellationToken)
    {
        try
        {
            Asset.ValidateOriginalName(request.OriginalName);
            if (request.ExpectedLength < 0) return ApiProblems.InvalidRequest();
            if (request.ExpectedSha256 is not null) StreamingContentHash.Normalize(request.ExpectedSha256);
        }
        catch (ArgumentException)
        {
            return ApiProblems.InvalidRequest();
        }

        var upload = await uploads.CreateAsync(AuthenticationEndpoints.UserId(context.User),
            Guid.Parse(context.User.FindFirstValue(AuthenticationConstants.DeviceIdClaim)!), request, cancellationToken);
        return Results.Created($"/api/uploads/{upload.Id}", upload);
    }

    private static async Task<IResult> PutChunkAsync(Guid id, int number, HttpContext context,
        IUploadService uploads, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return ApiProblems.Result(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Envie o chunk como application/octet-stream.");

        string? expectedHash = null;
        if (context.Request.Headers.TryGetValue("X-Chunk-SHA256", out var values))
        {
            if (values.Count != 1) return ApiProblems.InvalidRequest();
            try
            {
                expectedHash = StreamingContentHash.Normalize(values[0]!);
            }
            catch (ArgumentException)
            {
                return ApiProblems.InvalidRequest();
            }
        }

        var receipt = await uploads.PutChunkAsync(AuthenticationEndpoints.UserId(context.User), id, number,
            context.Request.Body, expectedHash, cancellationToken);
        return Results.Ok(receipt);
    }

    private static IResult NotFound() => ApiProblems.Result(StatusCodes.Status404NotFound,
        "resource_not_found", "Recurso não encontrado.");
}
