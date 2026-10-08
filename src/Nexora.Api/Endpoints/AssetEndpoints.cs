using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using System.Text.Json;
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
        group.MapPatch("/{id:guid}", UpdateAsync)
            .Produces<AssetSnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(503)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // Document the manually validated JSON body without an Accepts routing
                // constraint that would send empty PATCH requests to the fallback route.
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object, MinProperties = 1, AdditionalPropertiesAllowed = false,
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["originalName"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 255 },
                                    ["isFavorite"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                                }
                            }
                        }
                    }
                };
                return Task.CompletedTask;
            });
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, IAssetLifecycle lifecycle, CancellationToken cancellationToken) =>
            await lifecycle.MoveToTrashAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken)
                ? Results.NoContent() : NotFound())
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
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
        var trash = endpoints.MapGroup("/api/trash").WithTags("Trash").RequireAuthorization();
        trash.MapGet("", ListTrashAsync).Produces<AssetPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(503);
        trash.MapPost("/{id:guid}/restore", async (Guid id, HttpContext context, IAssetLifecycle lifecycle, CancellationToken cancellationToken) =>
        {
            var result = await lifecycle.RestoreAsync(AuthenticationEndpoints.UserId(context.User), id, cancellationToken);
            return result is null ? NotFound() : Results.Ok(result);
        }).Produces<AssetSnapshot>().ProducesProblem(401).ProducesProblem(404).ProducesProblem(503);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(int? limit, string? cursor, bool? imagesOnly, bool? isFavorite, string? sort, HttpContext context,
        IAssetLibrary library, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 100) return ApiProblems.InvalidRequest();
        var ordering = sort switch { null or "uploadedAt" => AssetSort.UploadedAt, "timeline" => AssetSort.Timeline, _ => (AssetSort)(-1) };
        var filters = new AssetListQuery(imagesOnly ?? false, isFavorite, ordering);
        try
        {
            filters.Validate();
            return Results.Ok(await library.ListAsync(AuthenticationEndpoints.UserId(context.User), pageSize, cursor, filters, cancellationToken));
        }
        catch (FormatException)
        {
            return ApiProblems.Result(StatusCodes.Status400BadRequest, "invalid_cursor", "O cursor informado é inválido.");
        }
        catch (ArgumentException) { return ApiProblems.InvalidRequest(); }
    }

    private static async Task<IResult> ListTrashAsync(int? limit, string? cursor, HttpContext context,
        IAssetLibrary library, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 100) return ApiProblems.InvalidRequest();
        try
        {
            return Results.Ok(await library.ListTrashAsync(AuthenticationEndpoints.UserId(context.User), pageSize, cursor, cancellationToken));
        }
        catch (FormatException)
        {
            return ApiProblems.Result(StatusCodes.Status400BadRequest, "invalid_cursor", "O cursor informado é inválido.");
        }
    }

    private static async Task<IResult> UpdateAsync(Guid id, HttpContext context,
        IAssetLifecycle lifecycle, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) return ApiProblems.InvalidRequest();
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken); }
        catch (JsonException) { return ApiProblems.InvalidRequest(); }
        using var parsed = document;
        var request = document.RootElement;
        if (request.ValueKind != JsonValueKind.Object) return ApiProblems.InvalidRequest();
        string? name = null;
        bool? favorite = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in request.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return ApiProblems.InvalidRequest();
            if (property.Name == "originalName" && property.Value.ValueKind == JsonValueKind.String)
                name = property.Value.GetString();
            else if (property.Name == "isFavorite" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                favorite = property.Value.GetBoolean();
            else return ApiProblems.InvalidRequest();
        }
        var update = new AssetUpdate(name, favorite);
        try { update.Validate(); }
        catch (ArgumentException) { return ApiProblems.InvalidRequest(); }
        var result = await lifecycle.UpdateAsync(AuthenticationEndpoints.UserId(context.User), id, update, cancellationToken);
        return result is null ? NotFound() : Results.Ok(result);
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
