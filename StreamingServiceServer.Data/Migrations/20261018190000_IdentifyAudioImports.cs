using Microsoft.EntityFrameworkCore.Migrations;

namespace StreamingServiceServer.Data.Migrations;

public partial class IdentifyAudioImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<Guid>(
        name: "ImportIdentifier", table: "PendingDownloads", type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "ImportIdentifier", table: "PendingDownloads");
}
