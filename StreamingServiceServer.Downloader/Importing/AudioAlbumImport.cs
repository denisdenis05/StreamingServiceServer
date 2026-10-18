using StreamingServiceDownloader.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceDownloader.Importing;

public sealed class AudioImportState
{
    public int Version { get; set; } = AudioAlbumImport.JournalVersion;
    public List<RecordingResponse> Expected { get; set; } = [];
    public List<AudioImportPublication> Published { get; set; } = [];
    public string SourceDirectory { get; set; } = string.Empty;
    public bool CleanupPending { get; set; }
    public bool CleanupCompleted { get; set; }
}

public sealed record AudioImportResult(int ExpectedTracks, int PublishedTracks, bool Complete);

public sealed class AudioAlbumImport(IAudioImportJournal journal, IAudioImportFiles files,
    Action<Guid, Exception> reportFailure)
{
    public const int JournalVersion = 1;
    private const int MaximumExpectedTracks = 1000;

    public async Task<AudioImportResult> RunAsync(
        string sourceDirectory,
        Func<CancellationToken, Task<IReadOnlyList<RecordingResponse>>> readExpected,
        Func<string, CancellationToken, Task<List<(string fileName, string fullPath)>>> readCandidates,
        Func<IReadOnlyList<RecordingResponse>, CancellationToken, Task> saveMetadata,
        Func<CancellationToken, Task> cleanupSource,
        Func<CancellationToken, Task> removeQueue,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? requireCurrent = null)
    {
        await using var ownership = await journal.AcquireAsync(cancellationToken);
        if (requireCurrent is not null) await requireCurrent(cancellationToken);
        var state = await journal.ReadAsync(cancellationToken);
        if (state is null)
        {
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.Directory) == 0)
                throw new IOException("Audio import source is not a directory.");
            var expected = await readExpected(cancellationToken);
            state = new AudioImportState { Expected = expected.ToList(), SourceDirectory = Path.GetFullPath(sourceDirectory) };
            Validate(state);
            await journal.WriteAsync(state, cancellationToken);
        }
        Validate(state);
        if (!state.CleanupCompleted)
        {
            var healthy = state.Published.Where(files.IsPublished).ToList();
            if (healthy.Count != state.Published.Count)
            {
                state.Published = healthy;
                state.CleanupPending = false;
                await journal.WriteAsync(state, cancellationToken);
            }
            var remaining = state.Expected.Where(recording => !healthy.Any(publication => publication.RecordingIdentifier == recording.Id)).ToList();
            if (remaining.Count > 0)
            {
                var usedSources = healthy.Select(publication => publication.SourcePath).ToHashSet(StringComparer.Ordinal);
                var candidates = (await readCandidates(state.SourceDirectory, cancellationToken))
                    .Where(candidate => !usedSources.Contains(Path.GetFullPath(candidate.fullPath))).ToList();
                var matches = SoulseekMatchingHelper.MatchSongsWithRecordings(remaining, candidates, cancellationToken: cancellationToken);
                foreach (var match in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (match.Score <= 0 || string.IsNullOrWhiteSpace(match.FullPath)) continue;
                    AudioImportPublication publication;
                    if (requireCurrent is not null) await requireCurrent(cancellationToken);
                    try { publication = await files.PublishAsync(match.Recording, match.FullPath, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
                    {
                        reportFailure(match.Recording.Id, failure);
                        continue;
                    }
                    state.Published.Add(publication);
                    await journal.WriteAsync(state, cancellationToken);
                }
            }
            if (state.Published.Count != state.Expected.Count)
                return new AudioImportResult(state.Expected.Count, state.Published.Count, false);
            if (!state.Published.All(files.IsPublished)) throw new IOException("An imported track changed before album completion.");
            if (!state.CleanupPending)
            {
                await saveMetadata(state.Expected, cancellationToken);
                state.CleanupPending = true;
                await journal.WriteAsync(state, cancellationToken);
            }
            if (requireCurrent is not null) await requireCurrent(cancellationToken);
            foreach (var publication in state.Published) await files.RemoveSourceAsync(publication, cancellationToken);
            await cleanupSource(cancellationToken);
            state.CleanupCompleted = true;
            await journal.WriteAsync(state, cancellationToken);
        }
        if (!state.Published.All(files.IsPublished)) throw new IOException("Imported audio is unavailable before queue removal.");
        if (requireCurrent is not null) await requireCurrent(cancellationToken);
        await removeQueue(cancellationToken);
        return new AudioImportResult(state.Expected.Count, state.Published.Count, true);
    }

    private static void Validate(AudioImportState state)
    {
        if (state.Version != JournalVersion || state.Expected is null || state.Published is null
            || !Path.IsPathFullyQualified(state.SourceDirectory)
            || state.Expected.Count == 0 || state.Expected.Count > MaximumExpectedTracks
            || state.Expected.Any(recording => recording.Id == Guid.Empty)
            || state.Expected.Select(recording => recording.Id).Distinct().Count() != state.Expected.Count
            || state.Published.Select(publication => publication.RecordingIdentifier).Distinct().Count() != state.Published.Count
            || state.Published.Any(publication => !Path.IsPathFullyQualified(publication.SourcePath)
                || !Path.GetFullPath(publication.SourcePath).StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.SourceDirectory)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            || state.Published.Any(publication => !state.Expected.Any(recording => recording.Id == publication.RecordingIdentifier))
            || ((state.CleanupPending || state.CleanupCompleted) && state.Published.Count != state.Expected.Count)
            || (state.CleanupCompleted && !state.CleanupPending))
            throw new InvalidDataException("Audio import journal has invalid or duplicate recording identities.");
    }
}
