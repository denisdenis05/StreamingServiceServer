using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizedCoverFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptimizedCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedSmallCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedVerySmallCover",
                table: "Releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedSmallCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedVerySmallCover",
                table: "Recordings",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptimizedCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "OptimizedSmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "OptimizedVerySmallCover",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "OptimizedCover",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "OptimizedSmallCover",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "OptimizedVerySmallCover",
                table: "Recordings");
        }
    }
}
