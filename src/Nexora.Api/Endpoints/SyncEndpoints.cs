using Nexora.Application.Content;

namespace Nexora.Api.Endpoints;

internal static class SyncEndpoints
{
    internal static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/sync").WithTags("Sync").RequireAuthorization();
        group.MapGet("", SnapshotAsync).Produces<SyncSnapshotPage>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(409).ProducesProblem(503);
        group.MapGet("/changes", ChangesAsync).Produces<SyncChangesPage>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    private static Task<IResult> SnapshotAsync(int? limit, string? cursor, HttpContext context,
        IAssetSync sync, CancellationToken cancellationToken) => ExecuteAsync(limit,
        () => sync.SnapshotAsync(AuthenticationEndpoints.UserId(context.User), limit ?? 50, cursor, cancellationToken));

    private static Task<IResult> ChangesAsync(int? limit, string? cursor, HttpContext context,
        IAssetSync sync, CancellationToken cancellationToken) => ExecuteAsync(limit,
        () => sync.ChangesAsync(AuthenticationEndpoints.UserId(context.User), limit ?? 50, cursor ?? "", cancellationToken));

    private static async Task<IResult> ExecuteAsync<T>(int? limit, Func<Task<T>> action)
    {
        if ((limit ?? 50) is < 1 or > 100) return ApiProblems.InvalidRequest();
        try { return Results.Ok(await action()); }
        catch (FormatException)
        {
            return ApiProblems.Result(StatusCodes.Status400BadRequest, "invalid_cursor", "O cursor informado é inválido.");
        }
        catch (SyncResetRequiredException)
        {
            return ApiProblems.Result(StatusCodes.Status409Conflict, "sync_reset_required", "Sincronize a biblioteca novamente.");
        }
    }
}
