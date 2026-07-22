using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadProviderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "PendingDownloads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SavePath",
                table: "PendingDownloads",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DownloadProviderConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PriorityOrder = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ApiUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Password = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ApiKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FormatPreferences = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExtraConfig = table.Column<string>(type: "text", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadProviderConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadProviderConfigs_ProviderName",
                table: "DownloadProviderConfigs",
                column: "ProviderName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadProviderConfigs");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "PendingDownloads");

            migrationBuilder.DropColumn(
                name: "SavePath",
                table: "PendingDownloads");
        }
    }
}
