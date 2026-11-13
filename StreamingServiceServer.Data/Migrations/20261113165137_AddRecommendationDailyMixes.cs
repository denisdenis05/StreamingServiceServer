using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationDailyMixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecommendationDailyMixes",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    SavedPlaylistId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationDailyMixes", x => new { x.UserId, x.Day });
                    table.ForeignKey(
                        name: "FK_RecommendationDailyMixes_Playlists_SavedPlaylistId",
                        column: x => x.SavedPlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecommendationDailyMixes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationDailyMixes_SavedPlaylistId",
                table: "RecommendationDailyMixes",
                column: "SavedPlaylistId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendationDailyMixes");
        }
    }
}
