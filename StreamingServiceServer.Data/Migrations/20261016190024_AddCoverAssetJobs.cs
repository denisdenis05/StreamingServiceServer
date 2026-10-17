using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    public partial class AddCoverAssetJobs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverAssetJobs",
                columns: table => new
                {
                    EntityKind = table.Column<int>(type: "integer", nullable: false),
                    EntityIdentifier = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAddress = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailure = table.Column<string>(type: "text", nullable: false),
                    AssetManifest = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverAssetJobs", x => new { x.EntityKind, x.EntityIdentifier });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverAssetJobs_CompletedAt_NextAttemptAt",
                table: "CoverAssetJobs",
                columns: new[] { "CompletedAt", "NextAttemptAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverAssetJobs");
        }
    }
}
