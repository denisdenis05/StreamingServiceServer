using System.Text;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Importing;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Models.MusicSearch;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StreamingServiceDownloader.BackgroundServices;
using StreamingServiceServer.Data.Models;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Data;
using StreamingServiceServer.Business.Services.Images;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioImportSafetyTests
{
    [PostgresFact]
    public async Task AdditiveMigrationAssignsDistinctImportIdentitiesToExistingPendingRows()
    {
        await using var fixture = await DatabaseFixture.CreateAsync(migrate: true);
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261017155458_PersistCoverBackfillCursor");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"PendingDownloads\" (\"Id\", \"SourceName\", \"ProviderName\") VALUES ({first}, 'one', 'Torrent'), ({second}, 'two', 'Torrent')");
        await migrator.MigrateAsync();
        var identities = await database.PendingDownloads.Select(download => download.ImportIdentifier).ToListAsync();
        Assert.Equal(2, identities.Count);
        Assert.DoesNotContain(Guid.Empty, identities);
        Assert.Equal(2, identities.Distinct().Count());
    }

    [Fact]
    public async Task NewImportGenerationDoesNotReuseAnOlderCompletedJournal()
    {
        using var fixture = new ImportFixture();
        await fixture.RunAsync();
        fixture.WriteSources();
        fixture.JournalOverride = new AudioImportJournal(fixture.MusicRoot, fixture.Album, "same-provider-new-import-generation");
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(2, fixture.ExpectedReads);
        Assert.Equal(2, fixture.Publisher.Calls[fixture.First.Id]);
        Assert.Equal(2, fixture.MetadataSaves);
    }
    [Fact]
    public void CompilationAlbumUsesAlbumArtistForTheSameFolderAsStreaming()
    {
        var recording = new RecordingDto { Id = Guid.NewGuid(), Title = "Song",
            ArtistCredit = [new ArtistCreditDto { Artist = new ArtistDto { Name = "Track artist" } }],
            Releases = [new ReleaseDto { Id = Guid.NewGuid(), Title = "Album", Artist = new ArtistDto { Name = "Various Artists" } }] };
        var expected = AudioImportCatalog.CreateExpected(recording, new FixtureImages());
        Assert.Equal("Various Artists", expected.ArtistName);
        AudioImportCatalog.RequireMatchingDestinations([expected], [new RecordingResponse { Id = expected.Id, ArtistName = "Various Artists", ReleaseTitle = "Album" }]);
    }

    [Fact]
    public void ChangedTrackSetOrDestinationMetadataBlocksCompletion()
    {
        var expected = new RecordingResponse { Id = Guid.NewGuid(), ArtistName = "Artist", ReleaseTitle = "Album" };
        Assert.Throws<IOException>(() => AudioImportCatalog.RequireMatchingDestinations([expected], []));
        Assert.Throws<IOException>(() => AudioImportCatalog.RequireMatchingDestinations([expected],
            [new RecordingResponse { Id = expected.Id, ArtistName = "Renamed", ReleaseTitle = "Album" }]));
    }
    [Fact]
    public async Task JournalFailureAfterPublicationRetainsSourceAndRetriesSafely()
    {
        using var fixture = new ImportFixture();
        fixture.JournalOverride = new FailingJournal(fixture.Journal());
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.True(File.Exists(fixture.FirstPath));
        Assert.Empty((await fixture.Journal().ReadAsync(default))!.Published);
        fixture.JournalOverride = null;
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(2, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks()
    {
        using var fixture = new ImportFixture();
        var publisher = new AudioImportFiles(fixture.MusicRoot);
        fixture.First.ArtistName = "../../outside";
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync(fixture.First, fixture.FirstPath, default));
        fixture.First.ArtistName = "Artist";
        Directory.CreateSymbolicLink(Path.Combine(fixture.MusicRoot, "ARTIST"), fixture.SourceRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync(fixture.First, fixture.FirstPath, default));
        Assert.True(File.Exists(fixture.FirstPath));
    }

    [PostgresFact]
    public async Task QueueRemovalIsAtomicAndCannotDeleteAReplacementSource()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var identifier = Guid.NewGuid();
        var pending = new PendingDownload { Id = identifier, SourceName = "new-source", ProviderName = "Torrent" };
        database.PendingDownloads.Add(pending);
        database.ReleasesToDownload.Add(new ReleaseToDownload { Id = identifier, Title = "Album", Artist = "Artist" });
        await database.SaveChangesAsync();
        var checker = new PendingDownloadChecker(null!, null!, new ConfigurationBuilder().Build());
        var remove = typeof(PendingDownloadChecker).GetMethod("RemoveAlbumFromDatabaseQueues", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var stale = new PendingDownload { Id = identifier, SourceName = "old-source", ProviderName = "Torrent" };
        await Assert.ThrowsAsync<IOException>(() => (Task)remove.Invoke(checker, [stale, database, CancellationToken.None])!);
        Assert.True(await database.PendingDownloads.AsNoTracking().AnyAsync(item => item.Id == identifier));
        Assert.True(await database.ReleasesToDownload.AsNoTracking().AnyAsync(item => item.Id == identifier));
        stale.SourceName = "new-source";
        await Assert.ThrowsAsync<IOException>(() => (Task)remove.Invoke(checker, [stale, database, CancellationToken.None])!);
        stale.ImportIdentifier = pending.ImportIdentifier;
        await (Task)remove.Invoke(checker, [stale, database, CancellationToken.None])!;
        Assert.False(await database.PendingDownloads.AsNoTracking().AnyAsync(item => item.Id == identifier));
        Assert.False(await database.ReleasesToDownload.AsNoTracking().AnyAsync(item => item.Id == identifier));
    }

    [Fact]
    public async Task SourceCleanupRequiresAnUnchangedSourceAndAHealthyPublishedDestination()
    {
        using var fixture = new ImportFixture();
        var publisher = new AudioImportFiles(fixture.MusicRoot);
        var publication = await publisher.PublishAsync(fixture.First, fixture.FirstPath, default);
        await File.WriteAllTextAsync(fixture.FirstPath, "a newer source");
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveSourceAsync(publication, default));
        Assert.True(File.Exists(fixture.FirstPath));
        File.Delete(fixture.Destination(fixture.First));
        await Assert.ThrowsAsync<IOException>(() => publisher.RemoveSourceAsync(publication, default));
        Assert.True(File.Exists(fixture.FirstPath));
    }

    [Fact]
    public async Task SuccessfulAlbumRemovesOnlyCommittedSourceFilesAfterMetadataWasSaved()
    {
        using var fixture = new ImportFixture();
        var extra = Path.Combine(fixture.SourceRoot, "unrelated.txt");
        await File.WriteAllTextAsync(extra, "keep");
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(1, fixture.MetadataSaves);
        Assert.False(File.Exists(fixture.FirstPath));
        Assert.False(File.Exists(fixture.SecondPath));
        Assert.True(File.Exists(extra));
    }

    [Fact]
    public async Task PartialAlbumRetainsSourceAndQueueAndRestartImportsOnlyMissingTrack()
    {
        using var fixture = new ImportFixture();
        File.Delete(fixture.SecondPath);
        var first = await fixture.RunAsync();
        Assert.False(first.Complete);
        Assert.Equal(1, first.PublishedTracks);
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.Equal(0, fixture.QueueRemovals);
        Assert.True(File.Exists(fixture.FirstPath));
        fixture.WriteSecond();
        var resumed = await fixture.RunAsync();
        Assert.True(resumed.Complete);
        Assert.Equal(1, fixture.ExpectedReads);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.First.Id]);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.Second.Id]);
        Assert.Equal(1, fixture.CleanupCalls);
        Assert.Equal(1, fixture.QueueRemovals);
    }

    [Fact]
    public async Task OneFailedPublicationCannotCompleteTheAlbumOrDeleteRemainingSource()
    {
        using var fixture = new ImportFixture();
        fixture.Publisher.FailIdentifier = fixture.Second.Id;
        var result = await fixture.RunAsync();
        Assert.False(result.Complete);
        Assert.Equal(1, result.PublishedTracks);
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.Equal(0, fixture.QueueRemovals);
        Assert.True(File.Exists(fixture.SecondPath));
        fixture.Publisher.FailIdentifier = null;
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task CleanupFailureRemainsDurableAndRetriesWithoutReadingCandidatesOrSavingMetadataAgain()
    {
        using var fixture = new ImportFixture();
        fixture.FailCleanup = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.QueueRemovals);
        var state = await fixture.Journal().ReadAsync(default);
        Assert.True(state!.CleanupPending);
        Assert.False(state.CleanupCompleted);
        fixture.FailCleanup = false;
        fixture.FailCandidateReads = true;
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(1, fixture.MetadataSaves);
        Assert.Equal(2, fixture.CleanupCalls);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task QueueFailureAfterCleanupResumesAtQueueRemovalWithoutCallingProviderAgain()
    {
        using var fixture = new ImportFixture();
        fixture.FailQueueRemoval = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        Assert.True((await fixture.Journal().ReadAsync(default))!.CleanupCompleted);
        fixture.FailQueueRemoval = false;
        fixture.FailCandidateReads = true;
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(1, fixture.CleanupCalls);
        Assert.Equal(1, fixture.MetadataSaves);
        Assert.Equal(2, fixture.QueueRemovals);
    }

    [Theory]
    [InlineData(AudioImportPhase.SavingMetadata, "metadata unavailable")]
    [InlineData(AudioImportPhase.CleaningProvider, "provider unavailable")]
    [InlineData(AudioImportPhase.RemovingQueue, "database unavailable")]
    public async Task ImportFailureRemainsVisibleAfterRestartWithoutItsOldLogs(AudioImportPhase phase, string message)
    {
        using var fixture = new ImportFixture();
        var pending = new PendingDownload { Id = fixture.Album, SourceName = "source", ProviderName = "Soulseek" };
        fixture.JournalOverride = AudioImportDiagnostics.CreateJournal(fixture.MusicRoot, pending);
        fixture.FailMetadata = phase == AudioImportPhase.SavingMetadata;
        fixture.FailCleanup = phase == AudioImportPhase.CleaningProvider;
        fixture.FailQueueRemoval = phase == AudioImportPhase.RemovingQueue;
        var diagnostics = new AudioImportDiagnostics(fixture.MusicRoot, pending);
        await Assert.ThrowsAsync<IOException>(() => diagnostics.TrackAsync(report => fixture.RunAsync(reportProgress: report), default));
        var restarted = new AudioImportDiagnostics(fixture.MusicRoot, pending);
        var progress = await restarted.ReadAsync(default);
        Assert.Equal(phase, progress!.Phase);
        Assert.Equal(message, progress.LastError);
        Assert.Equal(2, progress.ExpectedTracks);
        Assert.Equal(2, progress.PublishedTracks);
        fixture.FailMetadata = fixture.FailCleanup = fixture.FailQueueRemoval = false;
        Assert.True((await restarted.TrackAsync(report => fixture.RunAsync(reportProgress: report), default)).Complete);
        progress = await restarted.ReadAsync(default);
        Assert.Equal(AudioImportPhase.Completed, progress!.Phase);
        Assert.Null(progress.LastError);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task IndividualPublicationErrorIsRetainedWithPartialTrackCount()
    {
        using var fixture = new ImportFixture();
        var pending = new PendingDownload { Id = fixture.Album, SourceName = "source" };
        fixture.Publisher.FailIdentifier = fixture.Second.Id;
        var diagnostics = new AudioImportDiagnostics(fixture.MusicRoot, pending);
        var result = await diagnostics.TrackAsync(report => fixture.RunAsync(reportProgress: report), default);
        Assert.False(result.Complete);
        var progress = await diagnostics.ReadAsync(default);
        Assert.Equal(AudioImportPhase.ImportingTracks, progress!.Phase);
        Assert.Equal("disk unavailable", progress.LastError);
        Assert.Equal(1, progress.PublishedTracks);
        Assert.Equal(0, fixture.QueueRemovals);
    }

    [Fact]
    public async Task MissingSourceFailureBeforeJournalCreationIsPersisted()
    {
        using var fixture = new ImportFixture();
        Directory.Delete(fixture.SourceRoot, recursive: true);
        var pending = new PendingDownload { Id = fixture.Album, SourceName = "source" };
        var diagnostics = new AudioImportDiagnostics(fixture.MusicRoot, pending);
        await Assert.ThrowsAnyAsync<IOException>(() => diagnostics.TrackAsync(report => fixture.RunAsync(reportProgress: report), default));
        Assert.Null(await fixture.Journal().ReadAsync(default));
        var progress = await diagnostics.ReadAsync(default);
        Assert.Equal(AudioImportPhase.CheckingSource, progress!.Phase);
        Assert.False(string.IsNullOrWhiteSpace(progress.LastError));
    }

    [Fact]
    public async Task OlderJournalIdentifiesUnfinishedCleanupWithoutInventingAMissingError()
    {
        using var fixture = new ImportFixture();
        var pending = new PendingDownload { Id = fixture.Album, SourceName = "source" };
        fixture.JournalOverride = AudioImportDiagnostics.CreateJournal(fixture.MusicRoot, pending);
        fixture.FailCleanup = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        var diagnostics = new AudioImportDiagnostics(fixture.MusicRoot, pending);
        var progress = await diagnostics.ReadAsync(default);
        Assert.Equal(AudioImportPhase.Finalizing, progress!.Phase);
        Assert.Equal(2, progress.PublishedTracks);
        Assert.Null(progress.LastError);
        Assert.Contains("Tracks imported", progress.Description);
    }

    [Fact]
    public async Task MissingPublishedFileIsRepairedBeforeSourceCleanup()
    {
        using var fixture = new ImportFixture();
        File.Delete(fixture.SecondPath);
        await fixture.RunAsync();
        var state = await fixture.Journal().ReadAsync(default);
        File.Delete(Path.Combine(fixture.MusicRoot, state!.Published.Single().DestinationPath));
        fixture.WriteSecond();
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(2, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task MetadataSaveFailureRetainsAllSourceFilesAndDoesNotCleanProvider()
    {
        using var fixture = new ImportFixture();
        fixture.FailMetadata = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        Assert.True(File.Exists(fixture.FirstPath));
        Assert.True(File.Exists(fixture.SecondPath));
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.Equal(0, fixture.QueueRemovals);
        fixture.FailMetadata = false;
        Assert.True((await fixture.RunAsync()).Complete);
        Assert.Equal(1, fixture.Publisher.Calls[fixture.First.Id]);
    }

    [Fact]
    public async Task EmptyExpectedAlbumIsRejectedWithoutCleanup()
    {
        using var fixture = new ImportFixture();
        fixture.EmptyExpected = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync());
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.Equal(0, fixture.QueueRemovals);
    }

    [Fact]
    public async Task CancellationLeavesSourceAndPriorDestinationUnchanged()
    {
        using var fixture = new ImportFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var publisher = new AudioImportFiles(fixture.MusicRoot, _ => true, async (source, output, token) =>
        {
            await output.WriteAsync(new byte[] { 1, 2 }, token);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var destination = fixture.Destination(fixture.First);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "previous-complete-audio");
        var operation = publisher.PublishAsync(fixture.First, fixture.FirstPath, cancellation.Token);
        await entered.Task;
        Assert.Equal("previous-complete-audio", await File.ReadAllTextAsync(destination));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal("previous-complete-audio", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(fixture.FirstPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".audio-import-*"));
    }

    [Fact]
    public async Task CompletedStagingPublishesAtomicallyAndLeavesOriginalForAlbumCleanup()
    {
        using var fixture = new ImportFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new AudioImportFiles(fixture.MusicRoot, _ => true, async (source, output, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await source.CopyToAsync(output, token);
        });
        var operation = publisher.PublishAsync(fixture.First, fixture.FirstPath, default);
        await entered.Task;
        Assert.False(File.Exists(fixture.Destination(fixture.First)));
        release.SetResult();
        var publication = await operation;
        Assert.True(publisher.IsPublished(publication));
        Assert.Equal(await File.ReadAllBytesAsync(fixture.FirstPath), await File.ReadAllBytesAsync(fixture.Destination(fixture.First)));
        Assert.True(File.Exists(fixture.FirstPath));
    }

    [Fact]
    public async Task TruncatedOrInvalidCopyNeverReplacesCompleteDestination()
    {
        using var fixture = new ImportFixture();
        var destination = fixture.Destination(fixture.First);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old");
        var truncated = new AudioImportFiles(fixture.MusicRoot, _ => true, (source, output, token) => output.WriteAsync(new byte[] { 1 }, token).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => truncated.PublishAsync(fixture.First, fixture.FirstPath, default));
        var invalid = new AudioImportFiles(fixture.MusicRoot, _ => false);
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid.PublishAsync(fixture.First, fixture.FirstPath, default));
        Assert.Equal("old", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, ".audio-import-*"));
    }

    [Fact]
    public async Task RealAudioValidationRejectsTextAndAcceptsWaveFile()
    {
        using var fixture = new ImportFixture();
        var publisher = new AudioImportFiles(fixture.MusicRoot);
        var publication = await publisher.PublishAsync(fixture.First, fixture.FirstPath, default);
        Assert.True(publisher.IsPublished(publication));
        await File.WriteAllTextAsync(fixture.SecondPath, "invalid audio");
        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(fixture.Second, fixture.SecondPath, default));
        Assert.False(File.Exists(fixture.Destination(fixture.Second)));
    }

    [Fact]
    public async Task AlbumLockRejectsConcurrentSourceOwnersAndReleasesAfterFailure()
    {
        using var fixture = new ImportFixture();
        var journal = fixture.Journal();
        await using (await journal.AcquireAsync(default))
        {
            await Assert.ThrowsAsync<IOException>(() => new AudioImportJournal(fixture.MusicRoot, fixture.Album, "other-source").AcquireAsync(default));
        }
        await using var reacquired = await journal.AcquireAsync(default);
    }

    [Fact]
    public async Task StalePendingSourceCannotPublishOrRemoveQueue()
    {
        using var fixture = new ImportFixture();
        await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync(_ => Task.FromException(new IOException("superseded"))));
        Assert.Empty(fixture.Publisher.Calls);
        Assert.Equal(0, fixture.CleanupCalls);
        Assert.Equal(0, fixture.QueueRemovals);
    }

    [Fact]
    public void CandidateMetadataIsReadOncePerDistinctPhysicalPath()
    {
        var recordings = Enumerable.Range(1, 10).Select(position => new RecordingResponse { Id = Guid.NewGuid(), Title = "Song " + position, ReleaseTitle = "Album", PositionInAlbum = position }).ToArray();
        var candidates = recordings.Select((recording, index) => (fileName: (index + 1) + " Song " + (index + 1) + ".wav", fullPath: "/tmp/candidate-" + index + ".wav")).ToList();
        candidates.Add(candidates[0]);
        var reads = new Dictionary<string, int>();
        var matches = SoulseekMatchingHelper.MatchSongsWithRecordings(recordings, candidates, path =>
        {
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return null;
        });
        Assert.Equal(10, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Equal(10, matches.Count);
    }

    [Fact]
    public void MetadataInventoryHonorsCancellationBeforeFileReads()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SoulseekMatchingHelper.MatchSongsWithRecordings([], [("file", "/tmp/file")],
            _ => throw new InvalidOperationException("Unexpected read"), cancellation.Token));
    }

    private sealed class ImportFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "audio-import-tests-" + Guid.NewGuid());
        public Guid Album { get; } = Guid.NewGuid();
        public string MusicRoot => Path.Combine(root, "music");
        public string SourceRoot => Path.Combine(root, "downloads");
        public string FirstPath => Path.Combine(SourceRoot, "01 First.wav");
        public string SecondPath => Path.Combine(SourceRoot, "02 Second.wav");
        public RecordingResponse First { get; } = new() { Id = Guid.NewGuid(), Title = "First", ArtistName = "Artist", ReleaseTitle = "Album", PositionInAlbum = 1 };
        public RecordingResponse Second { get; } = new() { Id = Guid.NewGuid(), Title = "Second", ArtistName = "Artist", ReleaseTitle = "Album", PositionInAlbum = 2 };
        public CountingPublisher Publisher { get; }
        public bool FailCleanup { get; set; }
        public bool FailQueueRemoval { get; set; }
        public bool FailMetadata { get; set; }
        public bool FailCandidateReads { get; set; }
        public bool EmptyExpected { get; set; }
        public IAudioImportJournal? JournalOverride { get; set; }
        public int CleanupCalls { get; private set; }
        public int QueueRemovals { get; private set; }
        public int MetadataSaves { get; private set; }
        public int ExpectedReads { get; private set; }
        public ImportFixture()
        {
            Directory.CreateDirectory(MusicRoot);
            Directory.CreateDirectory(SourceRoot);
            WriteWave(FirstPath);
            WriteSecond();
            Publisher = new CountingPublisher(new AudioImportFiles(MusicRoot));
        }
        public void WriteSecond() => WriteWave(SecondPath);
        public void WriteSources() { WriteWave(FirstPath); WriteSecond(); }
        public string Destination(RecordingResponse recording) => Path.Combine(MusicRoot, "ARTIST", "ALBUM", recording.Id + ".wav");
        public AudioImportJournal Journal() => new(MusicRoot, Album, "fixture-source");
        public Task<AudioImportResult> RunAsync(Func<CancellationToken, Task>? requireCurrent = null,
            Func<AudioImportPhase, AudioImportState?, CancellationToken, Task>? reportProgress = null) =>
            new AudioAlbumImport(JournalOverride ?? Journal(), Publisher, (_, _) => { }).RunAsync(SourceRoot,
                _ => { ExpectedReads++; return Task.FromResult<IReadOnlyList<RecordingResponse>>(EmptyExpected ? [] : [First, Second]); },
                (directory, _) =>
                {
                    if (FailCandidateReads) throw new InvalidOperationException("Unexpected candidate read");
                    return Task.FromResult(Directory.GetFiles(directory).Select(path => (Path.GetFileName(path), path)).ToList());
                },
                (expected, _) => { MetadataSaves++; if (FailMetadata) throw new IOException("metadata unavailable"); return Task.CompletedTask; },
                _ => { CleanupCalls++; if (FailCleanup) throw new IOException("provider unavailable"); return Task.CompletedTask; },
                _ => { QueueRemovals++; if (FailQueueRemoval) throw new IOException("database unavailable"); return Task.CompletedTask; },
                default, requireCurrent, reportProgress);
        public void Dispose() => Directory.Delete(root, true);
        private static void WriteWave(string path)
        {
            const int sampleBytes = 1600;
            using var output = new BinaryWriter(File.Create(path));
            output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + sampleBytes); output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(8000); output.Write(16000);
            output.Write((short)2); output.Write((short)16); output.Write(Encoding.ASCII.GetBytes("data")); output.Write(sampleBytes); output.Write(new byte[sampleBytes]);
        }
    }

    private sealed class CountingPublisher(IAudioImportFiles inner) : IAudioImportFiles
    {
        public Dictionary<Guid, int> Calls { get; } = [];
        public Guid? FailIdentifier { get; set; }
        public bool IsPublished(AudioImportPublication publication) => inner.IsPublished(publication);
        public Task RemoveSourceAsync(AudioImportPublication publication, CancellationToken cancellationToken) => inner.RemoveSourceAsync(publication, cancellationToken);
        public Task<AudioImportPublication> PublishAsync(RecordingResponse recording, string path, CancellationToken cancellationToken)
        {
            Calls[recording.Id] = Calls.GetValueOrDefault(recording.Id) + 1;
            return recording.Id == FailIdentifier ? Task.FromException<AudioImportPublication>(new IOException("disk unavailable"))
                : inner.PublishAsync(recording, path, cancellationToken);
        }
    }

    private sealed class FailingJournal(IAudioImportJournal inner) : IAudioImportJournal
    {
        private int writes;
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) => inner.AcquireAsync(cancellationToken);
        public Task<AudioImportState?> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
        public Task WriteAsync(AudioImportState state, CancellationToken cancellationToken) => ++writes == 2
            ? Task.FromException(new IOException("journal unavailable")) : inner.WriteAsync(state, cancellationToken);
    }

    private sealed class FixtureImages : IImageService
    {
        public string GetPreferredCover(string? local, string? optimized, string? original) => local ?? optimized ?? original ?? string.Empty;
        public AlbumCoversDto GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null) => new();
        public AlbumCoversDto GetAllAlbumCovers(Guid releaseGroupId) => new();
    }
}
