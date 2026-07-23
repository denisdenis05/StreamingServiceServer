using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumAnalysisResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumAnalysisResults",
                columns: table => new
                {
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalTracks = table.Column<int>(type: "integer", nullable: false),
                    PresentTracks = table.Column<int>(type: "integer", nullable: false),
                    MissingTracks = table.Column<int>(type: "integer", nullable: false),
                    MissingRecordingIdsJson = table.Column<string>(type: "text", nullable: false),
                    LastAnalyzedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumAnalysisResults", x => x.AlbumId);
                    table.ForeignKey(
                        name: "FK_AlbumAnalysisResults_Releases_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumAnalysisResults");
        }
    }
}
