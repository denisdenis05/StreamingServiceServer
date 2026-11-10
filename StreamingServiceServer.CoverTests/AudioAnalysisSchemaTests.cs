using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Migrations;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisSchemaTests
{
    private const string DesignOnlyConnection = "Host=127.0.0.1;Port=1;Database=audio_analysis_schema_only;Username=design_only";
    private const string SettingsTableName = "AudioAnalysisSettings";
    private const string JobsTableName = "AudioAnalysisJobs";
    private const string AnalysesTableName = "RecordingAudioAnalyses";
    private const int SingletonRowCount = 1;
    private static readonly string[] ExpectedTableNames = [JobsTableName, SettingsTableName, AnalysesTableName];

    [Fact]
    public void MigrationCreatesOnlyAnalysisTablesAndSeedsDisabledSingleton()
    {
        var operations = new AddAudioAnalysisProcessing().UpOperations;
        var createdTables = operations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(ExpectedTableNames.Order(), createdTables.Select(table => table.Name).Order());
        var seed = Assert.Single(operations.OfType<InsertDataOperation>());
        Assert.Equal(SettingsTableName, seed.Table);
        Assert.Equal(SingletonRowCount, seed.Values.GetLength(0));
        var seedValues = seed.Columns.Select((column, position) => (column, value: seed.Values[0, position]))
            .ToDictionary(entry => entry.column, entry => entry.value);
        Assert.Equal(AudioAnalysisStoragePolicy.SettingsIdentifier, seedValues[nameof(AudioAnalysisSettings.Id)]);
        Assert.Equal(false, seedValues[nameof(AudioAnalysisSettings.Enabled)]);
        Assert.Equal(string.Empty, seedValues[nameof(AudioAnalysisSettings.ProtectedApiToken)]);
        Assert.Equal(string.Empty, seedValues[nameof(AudioAnalysisSettings.EndpointUrl)]);
        Assert.Equal(AudioAnalysisStoragePolicy.DefaultProfile, seedValues[nameof(AudioAnalysisSettings.Profile)]);
        Assert.Equal(AudioAnalysisStoragePolicy.InitialSettingsRevision, seedValues[nameof(AudioAnalysisSettings.Revision)]);
    }

    [Fact]
    public void SchemaKeepsOneJobAndOneCurrentResultPerRecordingWithCascadeRemoval()
    {
        using var database = CreateDatabase();
        foreach (var modelType in new[] { typeof(AudioAnalysisJob), typeof(RecordingAudioAnalysis) })
        {
            var entity = database.Model.FindEntityType(modelType)!;
            Assert.Equal(nameof(AudioAnalysisJob.RecordingId), Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
            var relationship = Assert.Single(entity.GetForeignKeys());
            Assert.Equal(typeof(Recording), relationship.PrincipalEntityType.ClrType);
            Assert.Equal(DeleteBehavior.Cascade, relationship.DeleteBehavior);
            Assert.True(relationship.IsUnique);
        }
    }

    [Fact]
    public void WorkerClaimsAndSettingsChangesUseConcurrencyTokensAndRunnableIndexes()
    {
        using var database = CreateDatabase();
        var settings = database.Model.FindEntityType(typeof(AudioAnalysisSettings))!;
        var jobs = database.Model.FindEntityType(typeof(AudioAnalysisJob))!;
        Assert.True(settings.FindProperty(nameof(AudioAnalysisSettings.Revision))!.IsConcurrencyToken);
        Assert.True(jobs.FindProperty(nameof(AudioAnalysisJob.Version))!.IsConcurrencyToken);
        Assert.Contains(jobs.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { nameof(AudioAnalysisJob.State), nameof(AudioAnalysisJob.NextAttemptAt) }));
        Assert.Contains(jobs.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { nameof(AudioAnalysisJob.State), nameof(AudioAnalysisJob.LeaseUntil) }));
    }

    [Fact]
    public void CompleteFeaturesAndEmbeddingMetadataAreRetainedAsStructuredJson()
    {
        using var database = CreateDatabase();
        var analyses = database.Model.FindEntityType(typeof(RecordingAudioAnalysis))!;
        foreach (var propertyName in new[] { nameof(RecordingAudioAnalysis.ModelsJson), nameof(RecordingAudioAnalysis.EmbeddingJson), nameof(RecordingAudioAnalysis.FeaturesJson) })
            Assert.Equal(AudioAnalysisStoragePolicy.JsonColumnType, analyses.FindProperty(propertyName)!.GetColumnType());
        Assert.NotNull(analyses.FindProperty(nameof(RecordingAudioAnalysis.EmbeddingDimensions)));
        Assert.NotNull(analyses.FindProperty(nameof(RecordingAudioAnalysis.FeatureSchemaVersion)));
        Assert.NotNull(analyses.FindProperty(nameof(RecordingAudioAnalysis.ProtocolVersion)));
        Assert.Equal(AudioAnalysisStoragePolicy.Sha256Length, analyses.FindProperty(nameof(RecordingAudioAnalysis.AudioSha256))!.GetMaxLength());
    }

    [Fact]
    public void CredentialAndFailureStorageHaveExplicitBounds()
    {
        using var database = CreateDatabase();
        var settings = database.Model.FindEntityType(typeof(AudioAnalysisSettings))!;
        var jobs = database.Model.FindEntityType(typeof(AudioAnalysisJob))!;
        Assert.Equal(AudioAnalysisStoragePolicy.EndpointUrlMaximumLength, settings.FindProperty(nameof(AudioAnalysisSettings.EndpointUrl))!.GetMaxLength());
        Assert.Equal(AudioAnalysisStoragePolicy.ProtectedApiTokenMaximumLength, settings.FindProperty(nameof(AudioAnalysisSettings.ProtectedApiToken))!.GetMaxLength());
        Assert.Equal(AudioAnalysisStoragePolicy.FailureCodeMaximumLength, jobs.FindProperty(nameof(AudioAnalysisJob.LastFailure))!.GetMaxLength());
        Assert.True(jobs.FindProperty(nameof(AudioAnalysisJob.LastFailure))!.IsNullable);
        var singletonConstraints = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AudioAnalysisSettings))!.GetCheckConstraints();
        Assert.Single(singletonConstraints);
    }

    [Fact]
    public void RollbackDropsOnlyAnalysisTables()
    {
        var operations = new AddAudioAnalysisProcessing().DownOperations;
        Assert.All(operations, operation => Assert.IsType<DropTableOperation>(operation));
        Assert.Equal(ExpectedTableNames.Order(), operations.Cast<DropTableOperation>().Select(operation => operation.Name).Order());
    }

    private static StreamingDbContext CreateDatabase() => new(new DbContextOptionsBuilder<StreamingDbContext>()
        .UseNpgsql(DesignOnlyConnection).Options);
}
