using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedListens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Listens",
                columns: table => new
                {
                    ListenId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrackDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    ServerCalculatedSeconds = table.Column<int>(type: "integer", nullable: false),
                    ClientReportedSeconds = table.Column<int>(type: "integer", nullable: false),
                    ValidatedPlayedSeconds = table.Column<int>(type: "integer", nullable: false),
                    NowPlayingReported = table.Column<bool>(type: "boolean", nullable: false),
                    Scrobbled = table.Column<bool>(type: "boolean", nullable: false),
                    ScrobbledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastProgressUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PauseSeekEvents = table.Column<string>(type: "text", nullable: true),
                    HasAnomalies = table.Column<bool>(type: "boolean", nullable: false),
                    AnomalyNotes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Listens", x => x.ListenId);
                    table.ForeignKey(
                        name: "FK_Listens_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Listens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listens_RecordingId",
                table: "Listens",
                column: "RecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_Listens_UserId",
                table: "Listens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Listens");
        }
    }
}
