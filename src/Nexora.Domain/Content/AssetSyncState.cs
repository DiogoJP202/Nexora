namespace Nexora.Domain.Content;

// These rows are written by PostgreSQL triggers in the same transaction as catalog changes.
public sealed class AssetSyncState
{
    private AssetSyncState() { }
    public Guid OwnerId { get; private set; }
    public Guid Epoch { get; private set; }
    public long Sequence { get; private set; }
}

public sealed class AssetSyncEntry
{
    private AssetSyncEntry() { }
    public Guid OwnerId { get; private set; }
    public long Sequence { get; private set; }
    public Guid AssetId { get; private set; }
    public string Kind { get; private set; } = null!;
    public string? Payload { get; private set; }
}
