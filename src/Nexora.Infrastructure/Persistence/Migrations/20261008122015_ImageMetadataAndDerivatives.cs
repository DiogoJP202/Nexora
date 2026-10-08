using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImageMetadataAndDerivatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BackgroundJobs_Kind",
                table: "BackgroundJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "UploadSessionId",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "BlobId",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlobImages",
                columns: table => new
                {
                    BlobId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    CapturedAtLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DerivativeGenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ThumbnailLength = table.Column<long>(type: "bigint", nullable: true),
                    ThumbnailSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    PreviewLength = table.Column<long>(type: "bigint", nullable: true),
                    PreviewSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobImages", x => x.BlobId);
                    table.CheckConstraint("CK_BlobImages_Metadata", "(\"State\" = 'Ready' AND \"Width\" IS NOT NULL AND \"Width\" > 0 AND \"Height\" IS NOT NULL AND \"Height\" > 0 AND \"ProcessedAt\" IS NOT NULL AND \"DerivativeGenerationId\" IS NOT NULL AND \"ThumbnailLength\" IS NOT NULL AND \"ThumbnailLength\" > 0 AND \"PreviewLength\" IS NOT NULL AND \"PreviewLength\" > 0 AND \"ThumbnailSha256\" IS NOT NULL AND \"ThumbnailSha256\" ~ '^[0-9a-f]{64}$' AND \"PreviewSha256\" IS NOT NULL AND \"PreviewSha256\" ~ '^[0-9a-f]{64}$') OR (\"State\" <> 'Ready' AND \"Width\" IS NULL AND \"Height\" IS NULL AND \"ProcessedAt\" IS NULL AND \"DerivativeGenerationId\" IS NULL AND \"ThumbnailLength\" IS NULL AND \"PreviewLength\" IS NULL AND \"ThumbnailSha256\" IS NULL AND \"PreviewSha256\" IS NULL AND \"CapturedAtLocal\" IS NULL AND \"CapturedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_BlobImages_Reservation", "\"ReservedBytes\" >= 0 AND (\"State\" <> 'Processing' OR \"ReservedBytes\" > 0)");
                    table.CheckConstraint("CK_BlobImages_State", "\"State\" IN ('Pending', 'Processing', 'Ready', 'Failed')");
                    table.ForeignKey(
                        name: "FK_BlobImages_Blobs_BlobId",
                        column: x => x.BlobId,
                        principalTable: "Blobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_BlobId",
                table: "BackgroundJobs",
                column: "BlobId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackgroundJobs_Kind",
                table: "BackgroundJobs",
                sql: "(\"Kind\" = 'FinalizeUpload' AND \"UploadSessionId\" IS NOT NULL AND \"BlobId\" IS NULL) OR (\"Kind\" = 'ProcessImage' AND \"BlobId\" IS NOT NULL AND \"UploadSessionId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_BlobImages_DerivativeGenerationId",
                table: "BlobImages",
                column: "DerivativeGenerationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlobImages_State",
                table: "BlobImages",
                column: "State");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundJobs_Blobs_BlobId",
                table: "BackgroundJobs",
                column: "BlobId",
                principalTable: "Blobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Image-only jobs have no upload target. Remove these new intents
            // (and their cascaded attempts) before restoring the phase-4 FK.
            migrationBuilder.Sql("DELETE FROM \"BackgroundJobs\" WHERE \"Kind\" = 'ProcessImage'");

            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundJobs_Blobs_BlobId",
                table: "BackgroundJobs");

            migrationBuilder.DropTable(
                name: "BlobImages");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_BlobId",
                table: "BackgroundJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BackgroundJobs_Kind",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "BlobId",
                table: "BackgroundJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "UploadSessionId",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BackgroundJobs_Kind",
                table: "BackgroundJobs",
                sql: "\"Kind\" = 'FinalizeUpload'");
        }
    }
}
