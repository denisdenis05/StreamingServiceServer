using System.Security.Cryptography;
using System.Text.Json;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceDownloader.Importing;

public sealed record AudioImportPublication(Guid RecordingIdentifier, string SourcePath, string DestinationPath,
    long Bytes, long ModifiedTicks, string Checksum);

public interface IAudioImportFiles
{
    bool IsPublished(AudioImportPublication publication);
    bool MatchesRecording(AudioImportPublication publication, RecordingResponse recording);
    Task QuarantineAsync(AudioImportPublication publication, CancellationToken cancellationToken);
    Task<AudioImportPublication> PublishAsync(RecordingResponse recording, string sourcePath, CancellationToken cancellationToken);
    Task RemoveSourceAsync(AudioImportPublication publication, CancellationToken cancellationToken);
}

public sealed class AudioImportFiles : IAudioImportFiles
{
    private const int CopyBufferBytes = 81920;
    private readonly string musicRoot;
    private readonly Func<string, bool> validateAudio;
    private readonly Func<Stream, Stream, CancellationToken, Task> copy;

    public AudioImportFiles(string musicRoot, Func<string, bool>? validateAudio = null,
        Func<Stream, Stream, CancellationToken, Task>? copy = null)
    {
        this.musicRoot = Path.GetFullPath(musicRoot);
        this.validateAudio = validateAudio ?? ValidateAudio;
        this.copy = copy ?? ((source, destination, cancellationToken) => source.CopyToAsync(destination, CopyBufferBytes, cancellationToken));
    }

    public bool IsPublished(AudioImportPublication publication)
    {
        var path = ContainedPath(publication.DestinationPath);
        var information = new FileInfo(path);
        return information.Exists && information.LinkTarget is null && information.Length == publication.Bytes
            && information.LastWriteTimeUtc.Ticks == publication.ModifiedTicks && publication.Bytes > 0;
    }

    public bool MatchesRecording(AudioImportPublication publication, RecordingResponse recording) =>
        StreamingServiceDownloader.Helpers.AudioMatchPolicy.TrackScore(recording, Path.GetFileName(publication.SourcePath),
            StreamingServiceDownloader.Helpers.AudioMetadataHelper.GetAudioMetadata(ContainedPath(publication.DestinationPath))) > 0;

    public Task QuarantineAsync(AudioImportPublication publication, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPublished(publication)) return Task.CompletedTask;
        var original = ContainedPath(publication.DestinationPath);
        var rejected = ContainedPath(Path.Combine(".audio-import-rejected", publication.RecordingIdentifier + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(original)));
        Directory.CreateDirectory(Path.GetDirectoryName(rejected)!);
        RequireOrdinaryDirectories(Path.GetDirectoryName(rejected)!);
        File.Move(original, rejected);
        return Task.CompletedTask;
    }

    public async Task RemoveSourceAsync(AudioImportPublication publication, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPublished(publication)) throw new IOException("Published audio changed before source removal.");
        if (Path.GetFullPath(publication.SourcePath) == ContainedPath(publication.DestinationPath)) return;
        var source = new FileInfo(publication.SourcePath);
        if (!source.Exists) return;
        if (source.LinkTarget is not null || source.Length != publication.Bytes
            || await ChecksumAsync(source.FullName, cancellationToken) != publication.Checksum)
            throw new IOException("Audio import source changed before cleanup.");
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(source.FullName);
    }

    public async Task<AudioImportPublication> PublishAsync(RecordingResponse recording, string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceInformation = new FileInfo(sourcePath);
        if (!sourceInformation.Exists || sourceInformation.Length <= 0 || sourceInformation.LinkTarget is not null)
            throw new InvalidDataException("Import source must be a nonempty regular audio file.");
        var sourceBytes = sourceInformation.Length;
        var sourceModified = sourceInformation.LastWriteTimeUtc;
        string relativeFolder;
        try { relativeFolder = MusicLocationHelper.GetRelativeFolderPathForRecording(recording); }
        catch (InvalidOperationException failure) { throw new InvalidDataException("Import destination is outside music storage.", failure); }
        var relativeDestination = Path.Combine(relativeFolder, recording.Id + sourceInformation.Extension.ToLowerInvariant());
        var destination = ContainedPath(relativeDestination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        RequireOrdinaryDirectories(directory);
        var staged = Path.Combine(directory, ".audio-import-" + Guid.NewGuid().ToString("N") + ".partial" + sourceInformation.Extension.ToLowerInvariant());
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                await copy(source, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            sourceInformation.Refresh();
            if (!sourceInformation.Exists || sourceInformation.Length != sourceBytes || sourceInformation.LastWriteTimeUtc != sourceModified
                || new FileInfo(staged).Length != sourceBytes || !validateAudio(staged))
                throw new InvalidDataException("Audio import source changed or staged output is invalid.");
            var stagedChecksum = await ChecksumAsync(staged, cancellationToken);
            if (stagedChecksum != await ChecksumAsync(sourcePath, cancellationToken))
                throw new InvalidDataException("Audio import copy checksum differs from its source.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, destination, overwrite: true);
            var published = new FileInfo(destination);
            return new AudioImportPublication(recording.Id, Path.GetFullPath(sourcePath),
                Path.GetRelativePath(musicRoot, destination), published.Length, published.LastWriteTimeUtc.Ticks, stagedChecksum);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private string ContainedPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Import destination must be relative.");
        var prefix = Path.EndsInDirectorySeparator(musicRoot) ? musicRoot : musicRoot + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(musicRoot, relativePath));
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Import destination is outside music storage.");
        RequireOrdinaryDirectories(Path.GetDirectoryName(path)!);
        return path;
    }

    private void RequireOrdinaryDirectories(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null) throw new InvalidDataException("Import destination cannot traverse symbolic links.");
            if (current.FullName == musicRoot) return;
        }
        throw new InvalidDataException("Import destination is outside music storage.");
    }

    private static bool ValidateAudio(string path)
    {
        using var audio = TagLib.File.Create(path);
        return (audio.Properties.MediaTypes & TagLib.MediaTypes.Audio) != 0 && audio.Properties.Duration > TimeSpan.Zero;
    }

    private static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

public interface IAudioImportJournal
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    Task<AudioImportState?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(AudioImportState state, CancellationToken cancellationToken);
}

public sealed class AudioImportJournal(string musicRoot, Guid albumIdentifier, string sourceIdentity) : IAudioImportJournal
{
    private const int MaximumJournalBytes = 4194304;
    private string JournalPath => Path.Combine(musicRoot, ".audio-import-state", albumIdentifier + "-"
        + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceIdentity))) + ".json");

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.GetAttributes(musicRoot);
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new InvalidDataException("Audio import journal directory cannot be a symbolic link.");
        var lockPath = Path.Combine(directory, albumIdentifier + ".lock");
        if (new FileInfo(lockPath).LinkTarget is not null) throw new InvalidDataException("Audio import lock cannot be a symbolic link.");
        return Task.FromResult<IAsyncDisposable>(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public async Task<AudioImportState?> ReadAsync(CancellationToken cancellationToken)
    {
        var information = new FileInfo(JournalPath);
        if (!information.Exists) return null;
        if (information.LinkTarget is not null || information.Length > MaximumJournalBytes) throw new InvalidDataException("Audio import journal is invalid.");
        await using var input = File.OpenRead(JournalPath);
        var state = await JsonSerializer.DeserializeAsync<AudioImportState>(input, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Audio import journal is empty.");
        if (state.LastProgressAt == default) state.LastProgressAt = information.LastWriteTimeUtc;
        return state;
    }

    public async Task WriteAsync(AudioImportState state, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > MaximumJournalBytes) throw new InvalidDataException("Audio import journal exceeds its size limit.");
        var directory = Path.GetDirectoryName(JournalPath)!;
        Directory.CreateDirectory(directory);
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new InvalidDataException("Audio import journal directory cannot be a symbolic link.");
        var staged = JournalPath + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            File.Move(staged, JournalPath, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
