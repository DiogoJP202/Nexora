using Microsoft.Net.Http.Headers;
using Nexora.Application.Content;
using Nexora.Application.Images;
using Nexora.Application.Storage;

namespace Nexora.Api.Endpoints;

internal static class AssetEndpoints
{
    internal static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/assets").WithTags("Assets").RequireAuthorization();
        group.MapGet("", ListAsync).Produces<AssetPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(503);
        group.MapGet("/{id:guid}", async (Guid id, HttpContext context, IAssetLibrary library, CancellationToken cancellationToken) =>
        {
            var asset = await library.FindAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken);
            return asset is null ? NotFound() : Results.Ok(asset);
        }).Produces<AssetSnapshot>().ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        group.MapMethods("/{id:guid}/content", [HttpMethods.Get, HttpMethods.Head], ContentAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status206PartialContent, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status304NotModified).Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        foreach (var kind in Enum.GetValues<DerivativeKind>())
        {
            var derivativeKind = kind;
            var route = kind == DerivativeKind.Thumbnail ? "thumbnail" : "preview";
            group.MapMethods($"/{{id:guid}}/{route}", [HttpMethods.Get, HttpMethods.Head],
                (Guid id, HttpContext context, IAssetDerivatives images, CancellationToken cancellationToken) =>
                    DerivativeAsync(id, derivativeKind, context, images, cancellationToken))
                .Produces(StatusCodes.Status200OK, contentType: "image/png")
                .Produces(StatusCodes.Status206PartialContent, contentType: "image/png")
                .Produces(StatusCodes.Status304NotModified).Produces(StatusCodes.Status416RangeNotSatisfiable)
                .ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        }
        endpoints.MapGet("/api/storage", async (HttpContext context, IStorageStatusService storage,
            CancellationToken cancellationToken) => Results.Ok(await storage.GetAsync(
                AuthenticationEndpoints.UserId(context.User), cancellationToken)))
            .WithTags("Storage").RequireAuthorization().Produces<StorageStatusSnapshot>().ProducesProblem(401).ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(int? limit, string? cursor, HttpContext context,
        IAssetLibrary library, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 100) return ApiProblems.InvalidRequest();
        try
        {
            return Results.Ok(await library.ListAsync(AuthenticationEndpoints.UserId(context.User), pageSize, cursor, cancellationToken));
        }
        catch (FormatException)
        {
            return ApiProblems.Result(StatusCodes.Status400BadRequest, "invalid_cursor", "O cursor informado é inválido.");
        }
    }

    private static async Task<IResult> ContentAsync(Guid id, HttpContext context, IAssetLibrary library,
        CancellationToken cancellationToken)
    {
        var result = await library.OpenContentAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken);
        if (result is null) return NotFound();
        return Results.Stream(result.Content, contentType: "application/octet-stream",
            fileDownloadName: result.Asset.OriginalName, lastModified: result.Asset.UploadedAt,
            entityTag: new EntityTagHeaderValue($"\"{result.BlobId:N}\""), enableRangeProcessing: true);
    }

    private static IResult NotFound() => ApiProblems.Result(StatusCodes.Status404NotFound,
        "resource_not_found", "Recurso não encontrado.");

    private static async Task<IResult> DerivativeAsync(Guid id, DerivativeKind kind, HttpContext context,
        IAssetDerivatives images, CancellationToken cancellationToken)
    {
        var result = await images.OpenAsync(AuthenticationEndpoints.UserId(context.User), id, kind, cancellationToken);
        if (result is null) return NotFound();
        context.Response.Headers.ContentDisposition = "inline";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return Results.Stream(result.Content, contentType: "image/png", lastModified: result.ProcessedAt,
            entityTag: new EntityTagHeaderValue($"\"{result.GenerationId:N}-{kind}\""), enableRangeProcessing: true);
    }
}
