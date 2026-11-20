using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLyricsLookupVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LookupVersion",
                table: "RecordingLyrics",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_RecordingLyrics_LookupVersion_State_LastFailure_NextAttempt~",
                table: "RecordingLyrics",
                columns: new[] { "LookupVersion", "State", "LastFailure", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecordingLyrics_LookupVersion_State_LastFailure_NextAttempt~",
                table: "RecordingLyrics");

            migrationBuilder.DropColumn(
                name: "LookupVersion",
                table: "RecordingLyrics");
        }
    }
}
