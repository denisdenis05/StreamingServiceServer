using System.Text.Json;
using Npgsql;

const string ConfigurationFlag = "--configuration-file";
const string OutputFlag = "--output";
const int RequiredArguments = 4;
const int ConfigurationFlagIndex = 0;
const int ConfigurationPathIndex = 1;
const int OutputFlagIndex = 2;
const int OutputPathIndex = 3;
const string ConnectionSection = "ConnectionStrings";
const string ConnectionName = "DefaultConnection";
const int MaximumRows = 20000;
const string MaximumRowsParameter = "maximumRows";
const string ReadOnlyOptions = "-c default_transaction_read_only=on -c statement_timeout=20000 -c lock_timeout=1000";
const string StatusQuery = """
SELECT json_build_object(
    'readOnly', current_setting('transaction_read_only'),
    'recordings', (SELECT count(*) FROM "Recordings"),
    'analyses', (SELECT count(*) FROM "RecordingAudioAnalyses"),
    'jobs', (SELECT json_object_agg("State", total) FROM (SELECT "State", count(*) total FROM "AudioAnalysisJobs" GROUP BY "State") counts),
    'distinctAudio', (SELECT count(DISTINCT "AudioSha256") FROM "RecordingAudioAnalyses"),
    'profiles', (SELECT json_agg(groups) FROM (SELECT "Profile", "EngineVersion", "FeatureSchemaVersion", "EmbeddingDimensions", "ModelsJson", count(*) total FROM "RecordingAudioAnalyses" GROUP BY "Profile", "EngineVersion", "FeatureSchemaVersion", "EmbeddingDimensions", "ModelsJson") groups)
)::text
""";
const string FeaturesQuery = """
SELECT json_build_object(
 'identifier', row_number() OVER (ORDER BY analysis."RecordingId"),
 'album', dense_rank() OVER (ORDER BY recording."ReleaseId"),
 'artist', dense_rank() OVER (ORDER BY release."ArtistId"),
 'content', dense_rank() OVER (ORDER BY analysis."AudioSha256"),
 'embedding', analysis."EmbeddingJson",
 'features', analysis."FeaturesJson",
 'profile', analysis."Profile",
 'engineVersion', analysis."EngineVersion",
 'models', analysis."ModelsJson"
)::text
FROM "RecordingAudioAnalyses" analysis
JOIN "Recordings" recording ON recording."Id" = analysis."RecordingId"
JOIN "Releases" release ON release."Id" = recording."ReleaseId"
ORDER BY analysis."RecordingId"
LIMIT @maximumRows
""";
try
{
    if (args.Length != RequiredArguments || args[ConfigurationFlagIndex] != ConfigurationFlag || args[OutputFlagIndex] != OutputFlag)
        throw new ArgumentException("Use --configuration-file <server settings file> --output <private temporary JSONL file>.");
    using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(args[ConfigurationPathIndex]));
    var configured = configuration.RootElement.GetProperty(ConnectionSection).GetProperty(ConnectionName).GetString();
    var builder = new NpgsqlConnectionStringBuilder(configured) { Options = ReadOnlyOptions, Timeout = 5, Pooling = false, ApplicationName = "AudioAnalysisReadOnlyEvaluation" };
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
    await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
    await readOnly.ExecuteNonQueryAsync();
    await using var status = new NpgsqlCommand(StatusQuery, connection, transaction);
    Console.WriteLine(await status.ExecuteScalarAsync());
    var outputPath = Path.GetFullPath(args[OutputPathIndex]);
    await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    await using var writer = new StreamWriter(output);
    await using var command = new NpgsqlCommand(FeaturesQuery, connection, transaction);
    command.Parameters.AddWithValue(MaximumRowsParameter, MaximumRows);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) await writer.WriteLineAsync(reader.GetString(0));
    await reader.DisposeAsync();
    await transaction.RollbackAsync();
}
catch (Exception failure)
{
    Console.WriteLine(JsonSerializer.Serialize(new { failure = failure.GetType().Name, sqlState = (failure as PostgresException)?.SqlState }));
    return 1;
}
return 0;
