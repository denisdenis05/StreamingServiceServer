using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSavedAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSavedAlbums",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSavedAlbums", x => new { x.UserId, x.ReleaseId });
                    table.ForeignKey(
                        name: "FK_UserSavedAlbums_Releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "Releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSavedAlbums_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSavedAlbums_ReleaseId",
                table: "UserSavedAlbums",
                column: "ReleaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSavedAlbums");
        }
    }
}
