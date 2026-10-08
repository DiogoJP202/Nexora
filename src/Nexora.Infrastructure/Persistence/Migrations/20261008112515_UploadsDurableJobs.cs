using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UploadsDurableJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ExpectedLength = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    ChunkSize = table.Column<int>(type: "integer", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    AssemblyTemporaryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssemblyLength = table.Column<long>(type: "bigint", nullable: true),
                    AssemblySha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    AssemblyMimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ResultAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                    table.CheckConstraint("CK_UploadSessions_Activity", "\"LastActivityAt\" >= \"CreatedAt\"");
                    table.CheckConstraint("CK_UploadSessions_Assembly", "(\"AssemblyTemporaryId\" IS NULL AND \"AssemblyLength\" IS NULL AND \"AssemblySha256\" IS NULL AND \"AssemblyMimeType\" IS NULL) OR (\"AssemblyTemporaryId\" IS NOT NULL AND \"AssemblyLength\" IS NOT NULL AND \"AssemblyLength\" = \"ExpectedLength\" AND \"AssemblySha256\" IS NOT NULL AND \"AssemblySha256\" ~ '^[0-9a-f]{64}$' AND \"AssemblyMimeType\" IS NOT NULL)");
                    table.CheckConstraint("CK_UploadSessions_Hash", "\"ExpectedSha256\" IS NULL OR \"ExpectedSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_UploadSessions_Length", "\"ExpectedLength\" BETWEEN 0 AND 4611686018427387903 AND \"ChunkSize\" BETWEEN 1 AND 67108864 AND \"ChunkCount\" BETWEEN 0 AND 65536 AND \"ChunkCount\" = (\"ExpectedLength\" + \"ChunkSize\" - 1) / \"ChunkSize\" AND \"ReservedBytes\" IN (0, \"ExpectedLength\" * 2)");
                    table.CheckConstraint("CK_UploadSessions_Result", "\"State\" <> 'Completed' OR \"ResultAssetId\" IS NOT NULL");
                    table.CheckConstraint("CK_UploadSessions_State", "\"State\" IN ('Open', 'Finalizing', 'Completed', 'Cancelled', 'Expired', 'Failed')");
                    table.ForeignKey(
                        name: "FK_UploadSessions_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadSessions_Assets_ResultAssetId",
                        column: x => x.ResultAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadSessions_Devices_OwnerId_DeviceId",
                        columns: x => new { x.OwnerId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "UserId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobs", x => x.Id);
                    table.CheckConstraint("CK_BackgroundJobs_Attempts", "\"Attempts\" >= 0 AND \"MaximumAttempts\" BETWEEN 1 AND 100 AND \"Attempts\" <= \"MaximumAttempts\"");
                    table.CheckConstraint("CK_BackgroundJobs_Kind", "\"Kind\" = 'FinalizeUpload'");
                    table.CheckConstraint("CK_BackgroundJobs_Lease", "\"State\" <> 'Running' OR (\"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_BackgroundJobs_State", "\"State\" IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_BackgroundJobs_UploadSessions_UploadSessionId",
                        column: x => x.UploadSessionId,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadChunks",
                columns: table => new
                {
                    UploadSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    TemporaryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadChunks", x => new { x.UploadSessionId, x.Number });
                    table.CheckConstraint("CK_UploadChunks_Hash", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_UploadChunks_NumberSize", "\"Number\" >= 0 AND \"Size\" > 0");
                    table.ForeignKey(
                        name: "FK_UploadChunks_UploadSessions_UploadSessionId",
                        column: x => x.UploadSessionId,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundJobAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundJobAttempts_BackgroundJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "BackgroundJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobAttempts_JobId",
                table: "BackgroundJobAttempts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_State_LeaseExpiresAt",
                table: "BackgroundJobs",
                columns: new[] { "State", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_State_NextAttemptAt",
                table: "BackgroundJobs",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_UploadSessionId",
                table: "BackgroundJobs",
                column: "UploadSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadChunks_TemporaryId",
                table: "UploadChunks",
                column: "TemporaryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_AssemblyTemporaryId",
                table: "UploadSessions",
                column: "AssemblyTemporaryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_OwnerId_DeviceId",
                table: "UploadSessions",
                columns: new[] { "OwnerId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_OwnerId_State",
                table: "UploadSessions",
                columns: new[] { "OwnerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ResultAssetId",
                table: "UploadSessions",
                column: "ResultAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_State_LastActivityAt",
                table: "UploadSessions",
                columns: new[] { "State", "LastActivityAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundJobAttempts");

            migrationBuilder.DropTable(
                name: "UploadChunks");

            migrationBuilder.DropTable(
                name: "BackgroundJobs");

            migrationBuilder.DropTable(
                name: "UploadSessions");
        }
    }
}
