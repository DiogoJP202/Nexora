using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Content;
using Nexora.Domain.Content;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Content;

public sealed class PostgresAssetSync(NexoraDbContext db, IDataProtectionProvider protection) : IAssetSync
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const byte SnapshotMode = 1;
    private const byte ChangesMode = 2;

    public async Task<SyncSnapshotPage> SnapshotAsync(Guid ownerId, int limit, string? cursor, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var anchor = cursor is null ? null : Decode(ownerId, SnapshotMode, cursor);
        var state = await StateAsync(ownerId, cancellationToken);
        if (anchor is not null) ValidateState(anchor, state);
        var watermark = anchor?.Sequence ?? state.Sequence;
        var lastId = anchor?.AssetId ?? Guid.Empty;
        // Historical payloads, including superseded and purged assets, make this boundary
        // stable across requests without keeping an MVCC transaction open on the server.
        var rows = await db.Database.SqlQuery<SnapshotRow>($"""
            SELECT "AssetId", "Payload"::text AS "Payload" FROM (
                SELECT DISTINCT ON ("AssetId") "AssetId", "Kind", "Payload"
                FROM "AssetSyncEntries"
                WHERE "OwnerId" = {ownerId} AND "Sequence" <= {watermark} AND "AssetId" > {lastId}
                ORDER BY "AssetId", "Sequence" DESC
            ) AS latest
            WHERE "Kind" = 'upsert'
            ORDER BY "AssetId"
            LIMIT {limit + 1}
            """).ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = more ? Encode(ownerId, SnapshotMode, state.Epoch, watermark, rows[^1].AssetId) : null;
        return new SyncSnapshotPage(rows.Select(row => Deserialize(row.Payload)).ToList(), next,
            Encode(ownerId, ChangesMode, state.Epoch, watermark, Guid.Empty));
    }

    public async Task<SyncChangesPage> ChangesAsync(Guid ownerId, int limit, string cursor, CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var anchor = Decode(ownerId, ChangesMode, cursor);
        var state = await StateAsync(ownerId, cancellationToken);
        ValidateState(anchor, state);
        var rows = await db.Set<AssetSyncEntry>().AsNoTracking()
            .Where(entry => entry.OwnerId == ownerId && entry.Sequence > anchor.Sequence && entry.Sequence <= state.Sequence)
            .OrderBy(entry => entry.Sequence).Take(limit + 1).ToListAsync(cancellationToken);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var position = rows.Count == 0 ? anchor.Sequence : rows[^1].Sequence;
        return new SyncChangesPage(rows.Select(entry => new SyncChange(entry.Sequence, entry.Kind, entry.AssetId,
            entry.Payload is null ? null : Deserialize(entry.Payload))).ToList(),
            Encode(ownerId, ChangesMode, state.Epoch, position, Guid.Empty), more);
    }

    private async Task<AssetSyncState> StateAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("A sync owner is required.", nameof(ownerId));
        var existing = await db.Set<AssetSyncState>().AsNoTracking().SingleOrDefaultAsync(state => state.OwnerId == ownerId, cancellationToken);
        if (existing is not null) return existing;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AssetSyncStates" ("OwnerId") VALUES ({ownerId}) ON CONFLICT ("OwnerId") DO NOTHING
            """, cancellationToken);
        return await db.Set<AssetSyncState>().AsNoTracking().SingleAsync(state => state.OwnerId == ownerId, cancellationToken);
    }

    private static AssetSnapshot Deserialize(string payload) =>
        JsonSerializer.Deserialize<AssetSnapshot>(payload, JsonOptions) ?? throw new InvalidOperationException("Invalid sync projection.");

    private string Encode(Guid ownerId, byte mode, Guid epoch, long sequence, Guid assetId)
    {
        Span<byte> bytes = stackalloc byte[42];
        bytes[0] = 1;
        bytes[1] = mode;
        epoch.TryWriteBytes(bytes[2..18]);
        BinaryPrimitives.WriteInt64BigEndian(bytes[18..26], sequence);
        assetId.TryWriteBytes(bytes[26..42]);
        return Protector(ownerId).Protect(Convert.ToBase64String(bytes));
    }

    private Anchor Decode(Guid ownerId, byte mode, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(character =>
            character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
            throw InvalidCursor();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Protector(ownerId).Unprotect(value)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { throw InvalidCursor(); }
        if (bytes.Length != 42 || bytes[0] != 1 || bytes[1] != mode) throw InvalidCursor();
        var epoch = new Guid(bytes.AsSpan(2, 16));
        var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(18, 8));
        var assetId = new Guid(bytes.AsSpan(26, 16));
        if (epoch == Guid.Empty || sequence < 0 || (mode == SnapshotMode && assetId == Guid.Empty)
            || (mode == ChangesMode && assetId != Guid.Empty)) throw InvalidCursor();
        return new Anchor(epoch, sequence, assetId);
    }

    private IDataProtector Protector(Guid ownerId) => protection.CreateProtector("Nexora.AssetSync", "v1", ownerId.ToString("N"));
    private static void ValidateState(Anchor anchor, AssetSyncState state)
    {
        if (anchor.Epoch != state.Epoch || anchor.Sequence > state.Sequence) throw new SyncResetRequiredException();
    }
    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private static FormatException InvalidCursor() => new("The sync cursor is invalid.");
    private sealed record Anchor(Guid Epoch, long Sequence, Guid AssetId);
    private sealed class SnapshotRow
    {
        public Guid AssetId { get; set; }
        public string Payload { get; set; } = null!;
    }
}
