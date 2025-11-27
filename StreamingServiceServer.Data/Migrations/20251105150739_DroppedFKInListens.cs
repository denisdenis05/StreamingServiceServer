using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class DroppedFKInListens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Listens_Recordings_RecordingId",
                table: "Listens");

            migrationBuilder.DropIndex(
                name: "IX_Listens_RecordingId",
                table: "Listens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
