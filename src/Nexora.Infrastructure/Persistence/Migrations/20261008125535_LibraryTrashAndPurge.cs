using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LibraryTrashAndPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UploadSessions_Result",
                table: "UploadSessions");

            migrationBuilder.DropIndex(
                name: "IX_Assets_OwnerId_DeletedAt",
                table: "Assets");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResultPurgedAt",
                table: "UploadSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UploadSessions_Result",
                table: "UploadSessions",
                sql: "(\"State\" = 'Completed' AND ((\"ResultAssetId\" IS NOT NULL AND \"ResultPurgedAt\" IS NULL) OR (\"ResultAssetId\" IS NULL AND \"ResultPurgedAt\" IS NOT NULL AND \"ResultPurgedAt\" >= \"LastActivityAt\"))) OR (\"State\" <> 'Completed' AND \"ResultAssetId\" IS NULL AND \"ResultPurgedAt\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_State_CreatedAt_Id",
                table: "Blobs",
                columns: new[] { "State", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_DeletedAt_Id",
                table: "Assets",
                columns: new[] { "DeletedAt", "Id" },
                filter: "\"DeletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_DeletedAt_Id",
                table: "Assets",
                columns: new[] { "OwnerId", "DeletedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_IsFavorite_UploadedAt_Id",
                table: "Assets",
                columns: new[] { "OwnerId", "IsFavorite", "UploadedAt", "Id" },
                descending: new[] { false, false, true, true },
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "UploadSessions" WHERE "ResultPurgedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot revert library schema after upload results have been purged; restore a consistent backup instead.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_UploadSessions_Result",
                table: "UploadSessions");

            migrationBuilder.DropIndex(
                name: "IX_Blobs_State_CreatedAt_Id",
                table: "Blobs");

            migrationBuilder.DropIndex(
                name: "IX_Assets_DeletedAt_Id",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_OwnerId_DeletedAt_Id",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_OwnerId_IsFavorite_UploadedAt_Id",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "ResultPurgedAt",
                table: "UploadSessions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UploadSessions_Result",
                table: "UploadSessions",
                sql: "\"State\" <> 'Completed' OR \"ResultAssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_DeletedAt",
                table: "Assets",
                columns: new[] { "OwnerId", "DeletedAt" });
        }
    }
}
