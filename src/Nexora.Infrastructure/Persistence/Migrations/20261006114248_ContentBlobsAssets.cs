using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentBlobsAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    DetectedMimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(44)", maxLength: 44, nullable: false),
                    State = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blobs", x => x.Id);
                    table.CheckConstraint("CK_Blobs_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_Blobs_Size", "\"Size\" >= 0");
                    table.CheckConstraint("CK_Blobs_State", "\"State\" IN ('Staging', 'Ready', 'Deleting')");
                    table.CheckConstraint("CK_Blobs_StorageKey", "\"StorageKey\" = 'blobs/' || substring(replace(\"Id\"::text, '-', ''), 1, 2) || '/' || substring(replace(\"Id\"::text, '-', ''), 3, 2) || '/' || replace(\"Id\"::text, '-', '')");
                });

            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.CheckConstraint("CK_Assets_DeletedAt", "\"DeletedAt\" IS NULL OR \"DeletedAt\" >= \"UploadedAt\"");
                    table.ForeignKey(
                        name: "FK_Assets_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Blobs_BlobId",
                        column: x => x.BlobId,
                        principalTable: "Blobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_BlobId",
                table: "Assets",
                column: "BlobId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_BlobId",
                table: "Assets",
                columns: new[] { "OwnerId", "BlobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_DeletedAt",
                table: "Assets",
                columns: new[] { "OwnerId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerId_UploadedAt_Id",
                table: "Assets",
                columns: new[] { "OwnerId", "UploadedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_Sha256",
                table: "Blobs",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "Blobs");
        }
    }
}
