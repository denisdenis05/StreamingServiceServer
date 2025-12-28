using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePlaylistRecordingFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistRecordings_Recordings_RecordingId",
                table: "PlaylistRecordings");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistRecordings_RecordingId",
                table: "PlaylistRecordings");

            migrationBuilder.AddColumn<string>(
                name: "Album",
                table: "PlaylistRecordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Artist",
                table: "PlaylistRecordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Cover",
                table: "PlaylistRecordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "PlaylistRecordings",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Album",
                table: "PlaylistRecordings");

            migrationBuilder.DropColumn(
                name: "Artist",
                table: "PlaylistRecordings");

            migrationBuilder.DropColumn(
                name: "Cover",
                table: "PlaylistRecordings");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "PlaylistRecordings");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistRecordings_RecordingId",
                table: "PlaylistRecordings",
                column: "RecordingId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistRecordings_Recordings_RecordingId",
                table: "PlaylistRecordings",
                column: "RecordingId",
                principalTable: "Recordings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
