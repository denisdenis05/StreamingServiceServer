using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Importing;

public sealed class QueuedAudioRecovery(string musicRoot, StreamingDbContext database,
    IExternalMusicSearchService metadataSource, IMetadataService metadata, IImageService images)
{
    public async Task<bool> TryCompleteAsync(ReleaseToDownload album, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(musicRoot, ".audio-import-state");
        if (!Directory.Exists(directory)) return false;
        var path = Directory.EnumerateFiles(directory, album.Id + "-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path is null) return false;
        var journal = AudioImportJournal.OpenExisting(musicRoot, album.Id, Path.GetFileName(path));
        var saved = await journal.ReadAsync(cancellationToken);
        if (saved is null || saved.CleanupCompleted) return false;
        async Task RequireCurrent(CancellationToken token)
        {
            if (await database.PendingDownloads.AnyAsync(item => item.Id == album.Id, token)
                || await database.FailedDownloads.AnyAsync(item => item.Id == album.Id, token)
                || !await database.ReleasesToDownload.AnyAsync(item => item.Id == album.Id, token))
                throw new IOException("Queued album ownership changed during local audio recovery.");
        }
        var import = new AudioAlbumImport(journal, new AudioImportFiles(musicRoot),
            (identifier, failure) => Console.WriteLine($"[MusicDownloader] Local recovery for {identifier}: {failure.Message}"));
        var result = await import.RunAsync(saved.SourceDirectory,
            async token => (await metadataSource.SearchAlbumRecordingsByIdAsync(album.Id).WaitAsync(token))
                .Select(recording => AudioImportCatalog.CreateExpected(recording, images)).ToArray(),
            (source, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(AudioImportCandidates.Read(source, musicRoot)); },
            async (expected, token) =>
            {
                await metadata.SearchAndSaveAlbumRecordingsByIdAsync(album.Id);
                var current = await database.Recordings.Where(recording => recording.Release.Id == album.Id)
                    .Select(recording => new RecordingResponse { Id = recording.Id, ArtistName = recording.Release.Artist.Name,
                        ReleaseTitle = recording.Release.Title }).ToListAsync(token);
                AudioImportCatalog.RequireMatchingDestinations(expected, current);
            },
            _ => Task.CompletedTask,
            async token =>
            {
                var removed = await database.ReleasesToDownload.Where(item => item.Id == album.Id
                    && !database.PendingDownloads.Any(pending => pending.Id == album.Id)
                    && !database.FailedDownloads.Any(failed => failed.Id == album.Id)).ExecuteDeleteAsync(token);
                if (removed != 1) throw new IOException("Queued album ownership changed before local recovery completion.");
            }, cancellationToken, RequireCurrent);
        if (result.Complete) Console.WriteLine($"[MusicDownloader] Completed {album.Id} from verified local audio; no replacement download needed.");
        return result.Complete;
    }
}
