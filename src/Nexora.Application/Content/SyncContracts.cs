namespace Nexora.Application.Content;

// Snapshot includes active and trashed items. Save Cursor only after NextCursor is null.
public sealed record SyncSnapshotPage(IReadOnlyList<AssetSnapshot> Items, string? NextCursor, string Cursor);

// A purge permanently removes the local row; an upsert replaces its complete metadata.
public sealed record SyncChange(long Sequence, string Kind, Guid AssetId, AssetSnapshot? Asset);
public sealed record SyncChangesPage(IReadOnlyList<SyncChange> Items, string Cursor, bool HasMore);

public interface IAssetSync
{
    Task<SyncSnapshotPage> SnapshotAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken);
    Task<SyncChangesPage> ChangesAsync(Guid ownerId, int limit, string cursor, CancellationToken cancellationToken);
}

public sealed class SyncResetRequiredException : Exception
{
    public SyncResetRequiredException() : base("The local library must be synchronized from a new snapshot.") { }
}
