using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedMultipleCoverSizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmallCover",
                table: "Releases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerySmallCover",
                table: "Releases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmallCover",
                table: "Recordings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerySmallCover",
                table: "Recordings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "VerySmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "SmallCover",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "VerySmallCover",
                table: "Recordings");
        }
    }
}
