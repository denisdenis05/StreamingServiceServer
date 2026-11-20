using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingLyricsQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LyricsQueueStates",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "text", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    NextRequestAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LyricsQueueStates", x => x.Provider);
                });

            migrationBuilder.CreateTable(
                name: "RecordingLyrics",
                columns: table => new
                {
                    RecordingIdentifier = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SourceRevision = table.Column<string>(type: "text", nullable: false),
                    ProviderIdentifier = table.Column<long>(type: "bigint", nullable: true),
                    PlainLyrics = table.Column<string>(type: "text", nullable: true),
                    SyncedLyrics = table.Column<string>(type: "text", nullable: true),
                    Lyricsfile = table.Column<string>(type: "text", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastFailure = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingLyrics", x => x.RecordingIdentifier);
                    table.ForeignKey(
                        name: "FK_RecordingLyrics_Recordings_RecordingId",
                        column: x => x.RecordingIdentifier,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingLyrics_NextAttemptAt_RecordingId",
                table: "RecordingLyrics",
                columns: new[] { "NextAttemptAt", "RecordingIdentifier" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LyricsQueueStates");

            migrationBuilder.DropTable(
                name: "RecordingLyrics");
        }
    }
}
