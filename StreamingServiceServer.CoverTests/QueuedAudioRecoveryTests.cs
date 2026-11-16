using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceDownloader.Importing;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class QueuedAudioRecoveryTests
{
    [PostgresFact]
    public Task VerifiedLocalAlbumFinishesWithoutAReplacementDownloadOrMetadataRequest() => RecoverAsync(false);

    [PostgresFact]
    public Task FeaturedCreditRecoveryCanFinishUsingRetainedAudioWithoutADownload() => RecoverAsync(true);

    private static async Task RecoverAsync(bool fromQuarantine)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var album = new ReleaseToDownload { Id = Guid.NewGuid(), Artist = "Artist", Title = "Album" };
        database.ReleasesToDownload.Add(album);
        await database.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "queued-audio-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "downloads");
            Directory.CreateDirectory(source);
            var path = Path.Combine(source, "01 Song.wav");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(1636); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
                writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(1600); writer.Write(new byte[1600]);
            }
            var recording = new RecordingResponse { Id = Guid.NewGuid(), ArtistName = "Artist", ReleaseTitle = "Album", Title = "Song" };
            var receipt = await new AudioImportFiles(root).PublishAsync(recording, path, default);
            if (fromQuarantine)
            {
                var rejected = Path.Combine(root, ".audio-import-rejected");
                Directory.CreateDirectory(rejected);
                var retained = Path.Combine(rejected, "retained.wav");
                File.Move(path, retained);
                using (var audio = TagLib.File.Create(retained))
                {
                    audio.Tag.Title = "Song (feat. Guest)";
                    audio.Tag.Performers = ["Artist"];
                    audio.Tag.Album = "Album";
                    audio.Save();
                }
                File.Delete(Path.Combine(root, receipt.DestinationPath));
                var release = new Release { Id = album.Id, Title = album.Title, Cover = string.Empty,
                    Artist = new Artist { Id = Guid.NewGuid(), Name = album.Artist, SortName = album.Artist } };
                database.Recordings.Add(new Recording { Id = recording.Id, Title = recording.Title!, Cover = string.Empty, Release = release });
                await database.SaveChangesAsync();
            }
            var journal = new AudioImportJournal(root, album.Id, "old-source");
            await journal.WriteAsync(new AudioImportState { Expected = [recording], Published = fromQuarantine ? [] : [receipt], SourceDirectory = source,
                CleanupPending = !fromQuarantine, MatchPolicyVersion = AudioAlbumImport.CurrentMatchPolicy, LastProgressAt = DateTimeOffset.UtcNow }, default);
            var metadata = DispatchProxy.Create<StreamingServiceServer.Business.Services.MusicSearch.IMetadataService, SavedMetadata>();
            var recovery = new QueuedAudioRecovery(root, database, null!, metadata, null!);
            Assert.True(await recovery.TryCompleteAsync(album, default));
            Assert.Empty(await database.ReleasesToDownload.ToListAsync());
            Assert.True(File.Exists(Path.Combine(root, receipt.DestinationPath)));
            Assert.True((await journal.ReadAsync(default))!.CleanupCompleted);

            // A later explicit redownload must not be cancelled by a completed old journal.
            database.ChangeTracker.Clear();
            database.ReleasesToDownload.Add(new ReleaseToDownload { Id = album.Id, Artist = album.Artist, Title = album.Title });
            await database.SaveChangesAsync();
            Assert.False(await recovery.TryCompleteAsync(album, default));
        }
        finally { Directory.Delete(root, true); }
    }

    public class SavedMetadata : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "SearchAndSaveAlbumRecordingsByIdAsync" ? Task.CompletedTask
                : throw new InvalidOperationException("Unexpected metadata call.");
    }

    [Fact]
    public void ExistingJournalCannotEscapeTheAlbumDirectory()
    {
        Assert.Throws<InvalidDataException>(() => AudioImportJournal.OpenExisting("/tmp", Guid.NewGuid(), "../another.json"));
    }
}
