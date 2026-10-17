using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenCoverAssetScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssetIdentity",
                table: "CoverAssetJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "InheritedSourceRevision",
                table: "CoverAssetJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OriginalIdentity",
                table: "CoverAssetJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "CoverAssetJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceRevision",
                table: "CoverAssetJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Suspended",
                table: "CoverAssetJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CoverMetadataRefreshes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Cursor = table.Column<Guid>(type: "uuid", nullable: true),
                    Pending = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedReleases = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    LastFailure = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverMetadataRefreshes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverAssetJobs_AssetIdentity",
                table: "CoverAssetJobs",
                column: "AssetIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_CoverAssetJobs_OriginalIdentity",
                table: "CoverAssetJobs",
                column: "OriginalIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_CoverAssetJobs_Suspended_CompletedAt_Priority_NextAttemptAt",
                table: "CoverAssetJobs",
                columns: new[] { "Suspended", "CompletedAt", "Priority", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverMetadataRefreshes");

            migrationBuilder.DropIndex(
                name: "IX_CoverAssetJobs_AssetIdentity",
                table: "CoverAssetJobs");

            migrationBuilder.DropIndex(
                name: "IX_CoverAssetJobs_OriginalIdentity",
                table: "CoverAssetJobs");

            migrationBuilder.DropIndex(
                name: "IX_CoverAssetJobs_Suspended_CompletedAt_Priority_NextAttemptAt",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "AssetIdentity",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "InheritedSourceRevision",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "OriginalIdentity",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "CoverAssetJobs");

            migrationBuilder.DropColumn(
                name: "Suspended",
                table: "CoverAssetJobs");
        }
    }
}
