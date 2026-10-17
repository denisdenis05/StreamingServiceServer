using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistCoverBackfillCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverBackfillStates",
                columns: table => new
                {
                    EntityKind = table.Column<int>(type: "integer", nullable: false),
                    PositionAddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PositionIdentifier = table.Column<Guid>(type: "uuid", nullable: true),
                    NextRestartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverBackfillStates", x => x.EntityKind);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverBackfillStates");
        }
    }
}
