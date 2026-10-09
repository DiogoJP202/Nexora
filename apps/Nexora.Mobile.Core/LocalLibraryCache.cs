namespace Nexora.Mobile.Core;

public sealed record CachedLibrary(string ScopeKey, string? Cursor, DateTimeOffset? LastSynchronizedAt,
    IReadOnlyList<AssetSnapshot> Items);

public sealed class LocalLibraryCache
{
    private readonly IPrivateFileStore files;
    private readonly NexoraClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    public LocalLibraryCache(string appPrivateRoot, NexoraClient client) : this(new AppPrivateFileStore(appPrivateRoot), client) { }
    public LocalLibraryCache(IPrivateFileStore files, NexoraClient client) { this.files = files; this.client = client; }
    private ServerScope Scope => client.Scope ?? throw new InvalidOperationException("Configure o servidor primeiro.");
    private static string CachePath(ServerScope scope) => $"library/{scope.Key}/metadata.json";

    public Task<CachedLibrary> GetCachedAsync(CancellationToken cancellationToken = default) =>
        client.ExecuteInScopeAsync(() => GetCachedCoreAsync(cancellationToken), cancellationToken);

    private async Task<CachedLibrary> GetCachedCoreAsync(CancellationToken cancellationToken)
    {
        var scope = Scope;
        var cached = await PrivateJson.ReadAsync<CachedLibrary>(files, CachePath(scope), cancellationToken);
        return cached is not null && cached.ScopeKey == scope.Key ? cached : new CachedLibrary(scope.Key, null, null, []);
    }

    public Task<CachedLibrary> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        client.ExecuteInScopeAsync(() => SynchronizeCoreAsync(cancellationToken), cancellationToken);

    private async Task<CachedLibrary> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var scope = Scope;
            var cached = await GetCachedAsync(cancellationToken);
            if (cached.Cursor is null) return await SnapshotAsync(scope, cancellationToken);
            var current = cached.Items.ToDictionary(asset => asset.Id);
            var cursor = cached.Cursor;
            try
            {
                bool more;
                long lastSequence = 0;
                do
                {
                    var page = await client.GetChangesAsync(cursor, cancellationToken);
                    foreach (var change in page.Items)
                    {
                        if (change.Sequence <= lastSequence) throw new InvalidDataException("Sequência de sincronização inválida.");
                        lastSequence = change.Sequence;
                        if (change.AssetId == Guid.Empty) throw new InvalidDataException("Mudança sem identificador.");
                        if (change.Kind == "purge" && change.Asset is null) current.Remove(change.AssetId);
                        else if (change.Kind == "upsert" && change.Asset?.Id == change.AssetId) current[change.AssetId] = change.Asset;
                        else throw new InvalidDataException("Mudança inválida.");
                    }
                    if (string.IsNullOrEmpty(page.Cursor) || (page.HasMore && page.Items.Count == 0)) throw new InvalidDataException("Cursor de sincronização inválido.");
                    cursor = page.Cursor;
                    more = page.HasMore;
                    EnsureScope(scope);
                    // Metadata and cursor are committed together; interruption replays safely.
                    var next = new CachedLibrary(scope.Key, cursor, DateTimeOffset.UtcNow, Order(current.Values));
                    await PrivateJson.WriteAsync(files, CachePath(scope), next, cancellationToken);
                } while (more);
            }
            catch (NexoraApiException error) when (error.Code is "invalid_cursor" or "sync_reset_required")
            {
                return await SnapshotAsync(scope, cancellationToken);
            }
            return new CachedLibrary(scope.Key, cursor, DateTimeOffset.UtcNow, Order(current.Values));
        }
        finally { gate.Release(); }
    }

    private async Task<CachedLibrary> SnapshotAsync(ServerScope scope, CancellationToken cancellationToken)
    {
        var current = new Dictionary<Guid, AssetSnapshot>();
        string? nextCursor = null;
        string? deltaCursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await client.GetSyncSnapshotAsync(nextCursor, cancellationToken);
            if (string.IsNullOrEmpty(page.Cursor)) throw new InvalidDataException("Snapshot sem cursor.");
            // Protected cursors are opaque and may use a fresh encryption nonce on
            // every response even when they represent the same frozen watermark.
            deltaCursor = page.Cursor;
            foreach (var item in page.Items) current[item.Id] = item;
            nextCursor = page.NextCursor;
            if (nextCursor is not null && !seen.Add(nextCursor)) throw new InvalidDataException("Página repetida.");
            EnsureScope(scope);
        } while (nextCursor is not null);
        var snapshot = new CachedLibrary(scope.Key, deltaCursor, DateTimeOffset.UtcNow, Order(current.Values));
        // A failed full refresh leaves the previous complete cache untouched.
        await PrivateJson.WriteAsync(files, CachePath(scope), snapshot, cancellationToken);
        return snapshot;
    }

    private void EnsureScope(ServerScope expected)
    {
        if (Scope.Key != expected.Key) throw new InvalidOperationException("A conta ou o servidor mudou durante a sincronização.");
    }
    private static AssetSnapshot[] Order(IEnumerable<AssetSnapshot> values) => values.OrderByDescending(asset => asset.UploadedAt).ThenBy(asset => asset.Id).ToArray();
}
