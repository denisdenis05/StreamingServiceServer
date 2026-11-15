using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Importing;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioImportPhase
{
    ReadingJournal,
    CheckingSource,
    ReadingMetadata,
    ImportingTracks,
    SavingMetadata,
    Finalizing,
    RemovingSourceFiles,
    CleaningProvider,
    RemovingQueue,
    Completed,
    Unavailable
}

public sealed record AudioImportProgress(AudioImportPhase Phase, int ExpectedTracks, int PublishedTracks,
    string? LastError, DateTimeOffset UpdatedAt)
{
    public string Description => Phase switch
    {
        AudioImportPhase.ReadingJournal => "Checking saved import progress",
        AudioImportPhase.CheckingSource => "Locating downloaded album files",
        AudioImportPhase.ReadingMetadata => "Loading album track metadata",
        AudioImportPhase.ImportingTracks => $"{PublishedTracks}/{ExpectedTracks} tracks imported",
        AudioImportPhase.SavingMetadata => "Tracks imported; saving library metadata",
        AudioImportPhase.Finalizing => "Tracks imported; source cleanup is unfinished",
        AudioImportPhase.RemovingSourceFiles => "Tracks imported; removing download files",
        AudioImportPhase.CleaningProvider => "Tracks imported; cleaning up the download source",
        AudioImportPhase.RemovingQueue => "Import complete; removing the album from the queue",
        AudioImportPhase.Completed => "Import complete",
        _ => "Import status unavailable"
    };

    public static AudioImportProgress FromJournal(AudioImportState state, DateTimeOffset now) => new(
        state.CleanupCompleted ? AudioImportPhase.RemovingQueue
            : state.CleanupPending ? AudioImportPhase.Finalizing
            : state.Published.Count == state.Expected.Count ? AudioImportPhase.SavingMetadata
            : AudioImportPhase.ImportingTracks,
        state.Expected.Count, state.Published.Count, state.LastPublicationError, now);
}

public sealed class AudioImportDiagnostics
{
    private const string DirectoryName = ".audio-import-state";
    private const int MaximumProgressBytes = 16384;
    public const int MaximumErrorCharacters = 2000;
    private readonly string _musicRoot;
    private readonly PendingDownload _source;
    private readonly string _path;
    private readonly Action<Exception> _reportFailure;
    private readonly TimeProvider _clock;

    public AudioImportDiagnostics(string musicRoot, PendingDownload source, Action<Exception>? reportFailure = null,
        TimeProvider? clock = null)
    {
        _musicRoot = musicRoot;
        _source = source;
        _reportFailure = reportFailure ?? (_ => { });
        _clock = clock ?? TimeProvider.System;
        var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SourceIdentity(source))));
        _path = Path.Combine(musicRoot, DirectoryName, source.Id + "-" + identity + ".status.json");
    }

    public static string SourceIdentity(PendingDownload source) => JsonSerializer.Serialize(new[]
        { source.ProviderName ?? "Torrent", source.SourceName, source.ImportIdentifier.ToString() });

    public static AudioImportJournal CreateJournal(string musicRoot, PendingDownload source) =>
        new(musicRoot, source.Id, SourceIdentity(source));

    internal static string DescribeFailure(Exception failure)
    {
        var cause = failure.GetBaseException();
        var message = ReferenceEquals(cause, failure) ? failure.Message : $"{failure.Message} {cause.Message}";
        return message[..Math.Min(message.Length, MaximumErrorCharacters)];
    }

    public async Task<AudioImportProgress?> ReadAsync(CancellationToken cancellationToken)
    {
        RequireOrdinaryDirectory();
        var information = new FileInfo(_path);
        if (information.Exists)
        {
            if (information.LinkTarget is not null || information.Length > MaximumProgressBytes)
                throw new InvalidDataException("Audio import status is invalid.");
            await using var input = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AudioImportProgress>(input, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Audio import status is empty.");
        }
        var journal = await CreateJournal(_musicRoot, _source).ReadAsync(cancellationToken);
        if (journal is not null) AudioAlbumImport.Validate(journal);
        return journal is null ? null : AudioImportProgress.FromJournal(journal, _clock.GetUtcNow());
    }

    public async Task<AudioImportResult> TrackAsync(
        Func<Func<AudioImportPhase, AudioImportState?, CancellationToken, Task>, Task<AudioImportResult>> operation,
        CancellationToken cancellationToken)
    {
        var current = new AudioImportProgress(AudioImportPhase.ReadingJournal, 0, 0, null, _clock.GetUtcNow());
        async Task Report(AudioImportPhase phase, AudioImportState? state, CancellationToken token)
        {
            current = new AudioImportProgress(phase, state?.Expected.Count ?? current.ExpectedTracks,
                state?.Published.Count ?? current.PublishedTracks, state?.LastPublicationError, _clock.GetUtcNow());
            await TryWriteAsync(current, token);
        }
        try
        {
            await TryWriteAsync(current, cancellationToken);
            return await operation(Report);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            await TryWriteAsync(current with { LastError = DescribeFailure(failure), UpdatedAt = _clock.GetUtcNow() }, CancellationToken.None);
            throw;
        }
    }

    private async Task TryWriteAsync(AudioImportProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            RequireOrdinaryDirectory();
            Directory.CreateDirectory(directory);
            RequireOrdinaryDirectory();
            var staged = _path + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await File.WriteAllBytesAsync(staged, JsonSerializer.SerializeToUtf8Bytes(progress), cancellationToken);
                File.Move(staged, _path, overwrite: true);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            _reportFailure(failure);
        }
    }

    private void RequireOrdinaryDirectory()
    {
        File.GetAttributes(_musicRoot);
        if (new DirectoryInfo(Path.GetDirectoryName(_path)!).LinkTarget is not null)
            throw new InvalidDataException("Audio import status directory cannot be a symbolic link.");
    }
}
