using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StreamingServiceServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioAnalysisProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudioAnalysisJobs",
                columns: table => new
                {
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastFailure = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioAnalysisJobs", x => x.RecordingId);
                    table.ForeignKey(
                        name: "FK_AudioAnalysisJobs_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AudioAnalysisSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProtectedApiToken = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioAnalysisSettings", x => x.Id);
                    table.CheckConstraint("CK_AudioAnalysisSettings_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "RecordingAudioAnalyses",
                columns: table => new
                {
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AudioSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EngineName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EngineVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    FeatureSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingDimensions = table.Column<int>(type: "integer", nullable: false),
                    ModelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EmbeddingJson = table.Column<string>(type: "jsonb", nullable: false),
                    FeaturesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Tempo = table.Column<double>(type: "double precision", nullable: true),
                    Energy = table.Column<double>(type: "double precision", nullable: true),
                    MusicalKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Scale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingAudioAnalyses", x => x.RecordingId);
                    table.ForeignKey(
                        name: "FK_RecordingAudioAnalyses_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AudioAnalysisSettings",
                columns: new[] { "Id", "Enabled", "EndpointUrl", "Profile", "ProtectedApiToken", "Revision", "UpdatedAt" },
                values: new object[] { 1, false, "", "musicnn-v1", "", new Guid("4980d5bd-497a-4eea-9ce2-2cdba3dfe2ec"), new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_AudioAnalysisJobs_NextAttemptAt_RecordingId",
                table: "AudioAnalysisJobs",
                columns: new[] { "NextAttemptAt", "RecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_AudioAnalysisJobs_State_LeaseUntil",
                table: "AudioAnalysisJobs",
                columns: new[] { "State", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_AudioAnalysisJobs_State_NextAttemptAt",
                table: "AudioAnalysisJobs",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingAudioAnalyses_Profile_AnalyzedAt",
                table: "RecordingAudioAnalyses",
                columns: new[] { "Profile", "AnalyzedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioAnalysisJobs");

            migrationBuilder.DropTable(
                name: "AudioAnalysisSettings");

            migrationBuilder.DropTable(
                name: "RecordingAudioAnalyses");
        }
    }
}
