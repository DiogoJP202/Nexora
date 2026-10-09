using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableAssetSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetSyncStates",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetSyncStates", x => x.OwnerId);
                    table.CheckConstraint("CK_AssetSyncStates_Sequence", "\"Sequence\" >= 0");
                    table.ForeignKey(
                        name: "FK_AssetSyncStates_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssetSyncEntries",
                columns: table => new
                {
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetSyncEntries", x => new { x.OwnerId, x.Sequence });
                    table.CheckConstraint("CK_AssetSyncEntries_Payload", "(\"Kind\" = 'upsert' AND \"Payload\" IS NOT NULL) OR (\"Kind\" = 'purge' AND \"Payload\" IS NULL)");
                    table.CheckConstraint("CK_AssetSyncEntries_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_AssetSyncEntries_AssetSyncStates_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AssetSyncStates",
                        principalColumn: "OwnerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssetSyncEntries_OwnerId_AssetId_Sequence",
                table: "AssetSyncEntries",
                columns: new[] { "OwnerId", "AssetId", "Sequence" },
                descending: new[] { false, false, true });

            migrationBuilder.Sql("""
                CREATE FUNCTION nexora_sync_projection(asset_id uuid) RETURNS jsonb
                LANGUAGE sql VOLATILE AS $$
                    SELECT jsonb_build_object(
                        'id', a."Id", 'originalName', a."OriginalName", 'size', b."Size",
                        'detectedMimeType', b."DetectedMimeType", 'uploadedAt', a."UploadedAt",
                        'isFavorite', a."IsFavorite", 'deletedAt', a."DeletedAt",
                        'image', CASE WHEN i."BlobId" IS NULL THEN NULL ELSE jsonb_build_object(
                            'state', CASE i."State" WHEN 'Pending' THEN 0 WHEN 'Processing' THEN 1 WHEN 'Ready' THEN 2 ELSE 3 END,
                            'width', i."Width", 'height', i."Height", 'capturedAtLocal', i."CapturedAtLocal",
                            'capturedAtUtc', i."CapturedAtUtc", 'processedAt', i."ProcessedAt",
                            'hasThumbnail', i."State" = 'Ready', 'hasPreview', i."State" = 'Ready',
                            'failureCode', i."FailureCode") END)
                    FROM "Assets" a JOIN "Blobs" b ON b."Id" = a."BlobId"
                    LEFT JOIN "BlobImages" i ON i."BlobId" = b."Id"
                    WHERE a."Id" = asset_id AND b."State" = 'Ready'
                $$;

                CREATE FUNCTION nexora_sync_append(owner_id uuid, asset_id uuid, payload jsonb) RETURNS void
                LANGUAGE plpgsql AS $$
                DECLARE next_sequence bigint;
                BEGIN
                    -- Cascading account deletion must not resurrect its journal.
                    IF NOT EXISTS (SELECT 1 FROM "AspNetUsers" WHERE "Id" = owner_id) THEN RETURN; END IF;
                    INSERT INTO "AssetSyncStates" ("OwnerId") VALUES (owner_id)
                    ON CONFLICT ("OwnerId") DO NOTHING;
                    -- This row lock is held until commit. A second writer cannot allocate
                    -- a later visible cursor while an earlier transaction is uncommitted.
                    -- Rollbacks undo both the counter and its events; no sequence gaps.
                    UPDATE "AssetSyncStates" SET "Sequence" = "Sequence" + 1
                    WHERE "OwnerId" = owner_id RETURNING "Sequence" INTO next_sequence;
                    INSERT INTO "AssetSyncEntries" ("OwnerId", "Sequence", "AssetId", "Kind", "Payload")
                    VALUES (owner_id, next_sequence, asset_id, CASE WHEN payload IS NULL THEN 'purge' ELSE 'upsert' END, payload);
                END $$;

                -- Backfill existing active and trashed assets under the migration's DDL locks.
                DO $$ DECLARE item record; BEGIN
                    FOR item IN SELECT a."Id", a."OwnerId" FROM "Assets" a
                        JOIN "Blobs" b ON b."Id" = a."BlobId" WHERE b."State" = 'Ready'
                        ORDER BY a."OwnerId", a."Id"
                    LOOP PERFORM nexora_sync_append(item."OwnerId", item."Id", nexora_sync_projection(item."Id")); END LOOP;
                END $$;

                CREATE FUNCTION nexora_sync_asset_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE payload jsonb;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        PERFORM nexora_sync_append(OLD."OwnerId", OLD."Id", NULL);
                        RETURN OLD;
                    END IF;
                    IF TG_OP = 'UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
                    IF TG_OP = 'UPDATE' AND NEW."OwnerId" <> OLD."OwnerId" THEN
                        RAISE EXCEPTION 'Asset ownership is immutable';
                    END IF;
                    payload := nexora_sync_projection(NEW."Id");
                    IF payload IS NOT NULL THEN PERFORM nexora_sync_append(NEW."OwnerId", NEW."Id", payload); END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER nexora_sync_assets AFTER INSERT OR UPDATE OR DELETE ON "Assets"
                    FOR EACH ROW EXECUTE FUNCTION nexora_sync_asset_trigger();

                CREATE FUNCTION nexora_sync_image_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE item record; blob_id uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN blob_id := OLD."BlobId"; ELSE blob_id := NEW."BlobId"; END IF;
                    IF TG_OP = 'UPDATE' AND ROW(NEW."State", NEW."Width", NEW."Height", NEW."CapturedAtLocal",
                        NEW."CapturedAtUtc", NEW."ProcessedAt", NEW."FailureCode") IS NOT DISTINCT FROM
                        ROW(OLD."State", OLD."Width", OLD."Height", OLD."CapturedAtLocal",
                        OLD."CapturedAtUtc", OLD."ProcessedAt", OLD."FailureCode") THEN RETURN NEW; END IF;
                    -- Shared blobs can belong to multiple owners. Always lock their counters
                    -- in UUID order, after the catalog's Blob / Image / Asset locks.
                    FOR item IN SELECT a."Id", a."OwnerId" FROM "Assets" a
                        JOIN "Blobs" b ON b."Id" = a."BlobId"
                        WHERE a."BlobId" = blob_id AND b."State" = 'Ready' ORDER BY a."OwnerId", a."Id"
                    LOOP PERFORM nexora_sync_append(item."OwnerId", item."Id", nexora_sync_projection(item."Id")); END LOOP;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                END $$;
                CREATE TRIGGER nexora_sync_images AFTER INSERT OR UPDATE OR DELETE ON "BlobImages"
                    FOR EACH ROW EXECUTE FUNCTION nexora_sync_image_trigger();

                CREATE FUNCTION nexora_sync_blob_trigger() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE item record;
                BEGIN
                    IF ROW(NEW."State", NEW."Size", NEW."DetectedMimeType") IS NOT DISTINCT FROM
                       ROW(OLD."State", OLD."Size", OLD."DetectedMimeType") THEN RETURN NEW; END IF;
                    FOR item IN SELECT "Id", "OwnerId" FROM "Assets" WHERE "BlobId" = NEW."Id" ORDER BY "OwnerId", "Id"
                    LOOP PERFORM nexora_sync_append(item."OwnerId", item."Id", nexora_sync_projection(item."Id")); END LOOP;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER nexora_sync_blobs AFTER UPDATE ON "Blobs"
                    FOR EACH ROW EXECUTE FUNCTION nexora_sync_blob_trigger();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER nexora_sync_blobs ON "Blobs";
                DROP TRIGGER nexora_sync_images ON "BlobImages";
                DROP TRIGGER nexora_sync_assets ON "Assets";
                DROP FUNCTION nexora_sync_blob_trigger();
                DROP FUNCTION nexora_sync_image_trigger();
                DROP FUNCTION nexora_sync_asset_trigger();
                DROP FUNCTION nexora_sync_append(uuid, uuid, jsonb);
                DROP FUNCTION nexora_sync_projection(uuid);
                """);
            migrationBuilder.DropTable(
                name: "AssetSyncEntries");

            migrationBuilder.DropTable(
                name: "AssetSyncStates");
        }
    }
}
