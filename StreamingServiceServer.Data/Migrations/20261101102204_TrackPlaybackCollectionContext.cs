using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackPlaybackCollectionContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Listens_UserId",
                table: "Listens");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceAlbumId",
                table: "Listens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Listens_UserId_PlaylistId_StartTime",
                table: "Listens",
                columns: new[] { "UserId", "PlaylistId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Listens_UserId_SourceAlbumId_StartTime",
                table: "Listens",
                columns: new[] { "UserId", "SourceAlbumId", "StartTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Listens_UserId_PlaylistId_StartTime",
                table: "Listens");

            migrationBuilder.DropIndex(
                name: "IX_Listens_UserId_SourceAlbumId_StartTime",
                table: "Listens");

            migrationBuilder.DropColumn(
                name: "SourceAlbumId",
                table: "Listens");

            migrationBuilder.CreateIndex(
                name: "IX_Listens_UserId",
                table: "Listens",
                column: "UserId");
        }
    }
}
