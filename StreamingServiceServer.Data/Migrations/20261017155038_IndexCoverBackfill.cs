using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexCoverBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Releases_DateAdded_Id",
                table: "Releases",
                columns: new[] { "DateAdded", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_DateAdded_Id",
                table: "Recordings",
                columns: new[] { "DateAdded", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Releases_DateAdded_Id",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_DateAdded_Id",
                table: "Recordings");
        }
    }
}
