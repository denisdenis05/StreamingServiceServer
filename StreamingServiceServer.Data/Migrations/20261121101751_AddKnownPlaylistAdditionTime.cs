using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownPlaylistAdditionTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "KnownAddedAt",
                table: "PlaylistRecordings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KnownAddedAt",
                table: "PlaylistRecordings");
        }
    }
}
