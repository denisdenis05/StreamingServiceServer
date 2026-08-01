using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnablePgTrgmFuzzySearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // GIN indexes allow similarity() and ILIKE '%x%' to use trigram index scans
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ix_recordings_title_trgm ON ""Recordings"" USING GIN (""Title"" gin_trgm_ops);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ix_releases_title_trgm  ON ""Releases""   USING GIN (""Title"" gin_trgm_ops);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ix_artists_name_trgm    ON ""Artists""    USING GIN (""Name""  gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_artists_name_trgm;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_releases_title_trgm;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ix_recordings_title_trgm;");
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm;");
        }
    }
}
