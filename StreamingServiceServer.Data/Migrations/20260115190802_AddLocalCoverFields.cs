using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalCoverFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LocalSmallCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LocalVerySmallCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LocalCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LocalSmallCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LocalVerySmallCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "LocalSmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "LocalVerySmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "LocalCover",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "LocalSmallCover",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "LocalVerySmallCover",
                table: "Recordings");
        }
    }
}
