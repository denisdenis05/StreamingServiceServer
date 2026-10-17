using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistCoverIntegritySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverAssetIntegrityStates",
                columns: table => new
                {
                    Identity = table.Column<string>(type: "text", nullable: false),
                    NextVerificationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverAssetIntegrityStates", x => x.Identity);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverAssetIntegrityStates_NextVerificationAt",
                table: "CoverAssetIntegrityStates",
                column: "NextVerificationAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverAssetIntegrityStates");
        }
    }
}
