using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedTrackMetadataToListen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Album",
                table: "Listens",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Artist",
                table: "Listens",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TrackName",
                table: "Listens",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Album",
                table: "Listens");

            migrationBuilder.DropColumn(
                name: "Artist",
                table: "Listens");

            migrationBuilder.DropColumn(
                name: "TrackName",
                table: "Listens");

            migrationBuilder.CreateIndex(
                name: "IX_Listens_RecordingId",
                table: "Listens",
                column: "RecordingId");

            migrationBuilder.AddForeignKey(
                name: "FK_Listens_Recordings_RecordingId",
                table: "Listens",
                column: "RecordingId",
                principalTable: "Recordings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
