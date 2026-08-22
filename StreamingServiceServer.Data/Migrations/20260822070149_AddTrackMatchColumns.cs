using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackMatchColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastMismatchCheckedAt",
                table: "AlbumAnalysisResults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MismatchedTracksCount",
                table: "AlbumAnalysisResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TrackMatchIssuesJson",
                table: "AlbumAnalysisResults",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastMismatchCheckedAt",
                table: "AlbumAnalysisResults");

            migrationBuilder.DropColumn(
                name: "MismatchedTracksCount",
                table: "AlbumAnalysisResults");

            migrationBuilder.DropColumn(
                name: "TrackMatchIssuesJson",
                table: "AlbumAnalysisResults");
        }
    }
}
