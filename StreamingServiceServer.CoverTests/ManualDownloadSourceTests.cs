using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Importing;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.Dashboard;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class ManualDownloadSourceTests
{
    private const string Username = "album-peer";
    private const string DirectoryPath = "Music/Album";
    private const long TrackSizeBytes = 1_000_000;
    private static readonly Guid SearchIdentifier = Guid.Parse("827351c4-5647-4ed9-901e-62544f800197");
    private static readonly SlskdFile[] Tracks =
        [new() { Filename = DirectoryPath + "/01.flac", Size = TrackSizeBytes },
         new() { Filename = DirectoryPath + "/02.flac", Size = TrackSizeBytes }];

    [Fact]
    public void SelectionIncludesOnlyUnlockedAudioFromTheSelectedPeerAndDirectory()
    {
        var search = Search();
        search.Responses![0].Files!.AddRange([
            new() { Filename = DirectoryPath + "/locked.flac", Size = TrackSizeBytes, IsLocked = true },
            new() { Filename = DirectoryPath + "/cover.jpg", Size = TrackSizeBytes },
            new() { Filename = DirectoryPath + "/empty.mp3" },
            new() { Filename = "Music/Other/03.flac", Size = TrackSizeBytes }]);
        search.Responses.Add(new() { Username = "other-peer", Files = [Tracks[0]] });
        Assert.Equal(Tracks.Select(track => track.Filename),
            SlskdSourceSelection.GetFiles(search, Username, "Music\\Album").Select(track => track.Filename));
    }

    [Fact]
    public void SelectionRejectsIncompleteDuplicateAndTamperedFiles()
    {
        var expected = SlskdSourceSelection.GetFiles(Search(), Username, DirectoryPath);
        Assert.True(SlskdSourceSelection.MatchesFiles(expected, expected.Reverse().ToArray()));
        Assert.False(SlskdSourceSelection.MatchesFiles(expected, [expected[0]]));
        Assert.False(SlskdSourceSelection.MatchesFiles(expected, [expected[0], expected[0]]));
        Assert.False(SlskdSourceSelection.MatchesFiles(expected,
            [expected[0], new() { Filename = expected[1].Filename, Size = TrackSizeBytes + 1 }]));
        Assert.False(SlskdSourceSelection.MatchesFiles([], []));
    }

    [Fact]
    public void ManualSourceEndpointsRequireAnAdministrator()
    {
        foreach (var method in new[] { nameof(DashboardController.SearchSlskdSources), nameof(DashboardController.AddSlskdSource) })
            Assert.Contains(typeof(DashboardController).GetMethod(method)!.GetCustomAttributes(typeof(AuthorizeAttribute), false)
                .Cast<AuthorizeAttribute>(), attribute => attribute.Roles == "Admin");
    }

    [PostgresFact]
    public async Task AddingSourceToFailedAlbumClearsFailureAndRetainsQueuedRelease()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.False(await fixture.Database.FailedDownloads.AnyAsync());
        Assert.True(await fixture.Database.ReleasesToDownload.AnyAsync(album => album.Id == fixture.AlbumIdentifier));
        var pending = await fixture.Database.PendingDownloads.SingleAsync();
        Assert.Equal("Soulseek", pending.ProviderName);
        Assert.Equal(DirectoryPath, pending.SavePath);
        Assert.Equal(Tracks.Length, SlskdSourceIdentity.Parse(pending.SourceName)!.Files.Count);
        Assert.Equal(Tracks.Length, Assert.Single(fixture.Handler.Submissions).Count);
        Assert.Equal("downloading", Json(await fixture.Controller.GetQueuedAlbums(default))[0].GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task AddingSourceToQueuedAlbumCreatesPendingDownload()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Single(await fixture.Database.PendingDownloads.ToListAsync());
        Assert.Empty(await fixture.Database.FailedDownloads.ToListAsync());
    }

    [PostgresFact]
    public async Task FailedAlbumWithoutQueueRecordIsRestoredToQueue()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        fixture.Database.ReleasesToDownload.Remove(await fixture.Database.ReleasesToDownload.SingleAsync());
        await fixture.Database.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Equal("Album", (await fixture.Database.ReleasesToDownload.SingleAsync()).Title);
        Assert.Empty(await fixture.Database.FailedDownloads.ToListAsync());
    }

    [PostgresFact]
    public async Task PartialEnqueueRetainsFailedAndQueuedState()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        fixture.Handler.AcceptOnlyFirstTrack = true;
        var result = Assert.IsType<ObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Equal(502, result.StatusCode);
        Assert.True(await fixture.Database.FailedDownloads.AnyAsync());
        Assert.True(await fixture.Database.ReleasesToDownload.AnyAsync());
        Assert.False(await fixture.Database.PendingDownloads.AnyAsync());
    }

    [PostgresFact]
    public async Task ExistingSourceAndRepeatedSubmissionCannotBeOverwritten()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default);
        var original = await fixture.Database.PendingDownloads.SingleAsync();
        var generation = original.ImportIdentifier;
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Single(fixture.Handler.Submissions);
        Assert.Equal(generation, original.ImportIdentifier);
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.SearchSlskdSources(fixture.AlbumIdentifier, new(), default));
    }

    [PostgresFact]
    public async Task MissingSearchAndTamperedFilesDoNotClearFailure()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        fixture.Handler.SearchAvailable = false;
        Assert.Equal(502, Assert.IsType<ObjectResult>(await fixture.Controller.AddSlskdSource(
            fixture.AlbumIdentifier, Request(), default)).StatusCode);
        fixture.Handler.SearchAvailable = true;
        var request = Request();
        request.Files[0].Size++;
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, request, default));
        Assert.True(await fixture.Database.FailedDownloads.AnyAsync());
        Assert.False(await fixture.Database.PendingDownloads.AnyAsync());
        Assert.Empty(fixture.Handler.Submissions);
    }

    [PostgresFact]
    public async Task AlbumLockPreventsManualAssignmentDuringAnAutomaticSearch()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        await using var ownership = await new AudioImportJournal(fixture.MusicRoot,
            fixture.AlbumIdentifier, "automatic-source").AcquireAsync(default);
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Empty(fixture.Handler.Submissions);
        Assert.False(await fixture.Database.PendingDownloads.AnyAsync());
    }

    [PostgresFact]
    public async Task EmptyPendingSourceRequiresItsCurrentGeneration()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        var pending = new PendingDownload { Id = fixture.AlbumIdentifier };
        fixture.Database.PendingDownloads.Add(pending);
        await fixture.Database.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        var request = Request();
        request.ExpectedImportIdentifier = pending.ImportIdentifier;
        var previousGeneration = pending.ImportIdentifier;
        Assert.IsType<OkObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, request, default));
        Assert.NotEqual(previousGeneration, pending.ImportIdentifier);
        Assert.False(await fixture.Database.FailedDownloads.AnyAsync());
    }

    [PostgresFact]
    public async Task FreshSearchUsesAlbumMetadataAndDoesNotChangeFailedState()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        var result = Json(await fixture.Controller.SearchSlskdSources(fixture.AlbumIdentifier, new(), default));
        Assert.Equal("Artist Album", fixture.Handler.Query);
        Assert.NotEqual(Guid.Empty, result.GetProperty("searchIdentifier").GetGuid());
        Assert.Equal(Guid.Empty, result.GetProperty("importIdentifier").GetGuid());
        Assert.Single(result.GetProperty("sources").EnumerateArray());
        Assert.True(await fixture.Database.FailedDownloads.AnyAsync());
        Assert.False(await fixture.Database.PendingDownloads.AnyAsync());
    }

    [PostgresFact]
    public async Task SearchUsesCustomTermsAndReportsProviderFailuresWithoutChangingQueue()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync(failed: true);
        fixture.Handler.RejectSearch = true;
        var result = Assert.IsType<ObjectResult>(await fixture.Controller.SearchSlskdSources(fixture.AlbumIdentifier,
            new() { Query = "  alternate artist album  " }, default));
        Assert.Equal(502, result.StatusCode);
        Assert.Equal("alternate artist album", fixture.Handler.Query);
        Assert.True(await fixture.Database.FailedDownloads.AnyAsync());
    }

    [PostgresFact]
    public async Task SearchAndAssignmentRejectRemovedAlbums()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        fixture.Database.ReleasesToDownload.Remove(await fixture.Database.ReleasesToDownload.SingleAsync());
        await fixture.Database.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.SearchSlskdSources(fixture.AlbumIdentifier, new(), default));
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.AddSlskdSource(fixture.AlbumIdentifier, Request(), default));
        Assert.Empty(fixture.Handler.Submissions);
    }

    [PostgresFact]
    public async Task SingleSongRetryRejectsARecordingFromAnotherAlbum()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        var recording = await AddRecordingAsync(fixture);
        Assert.IsType<NotFoundObjectResult>(await fixture.Controller.StartRecordingRedownload(
            Guid.NewGuid(), recording.Id, default));
        Assert.Empty(fixture.Handler.Submissions);
    }

    [PostgresFact]
    public async Task SingleSongRetryRejectsDuplicateActiveJobsAndScopesStatusToItsAlbum()
    {
        await using var fixture = await ManualSourceFixture.CreateAsync();
        var recording = await AddRecordingAsync(fixture);
        var jobs = (ConcurrentDictionary<Guid, RecordingRedownloadJob>)typeof(DashboardController)
            .GetField("_recordingJobs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        jobs[recording.Id] = new RecordingRedownloadJob { AlbumIdentifier = recording.Release.Id, State = "downloading" };
        try
        {
            Assert.IsType<ConflictObjectResult>(await fixture.Controller.StartRecordingRedownload(
                recording.Release.Id, recording.Id, default));
            Assert.Equal("idle", Json(fixture.Controller.GetRecordingRedownloadStatus(Guid.NewGuid(), recording.Id))
                .GetProperty("state").GetString());
            Assert.Equal("downloading", Json(fixture.Controller.GetRecordingRedownloadStatus(recording.Release.Id, recording.Id))
                .GetProperty("State").GetString());
            Assert.Empty(fixture.Handler.Submissions);
        }
        finally
        {
            jobs.TryRemove(recording.Id, out _);
        }
    }

    private static async Task<Recording> AddRecordingAsync(ManualSourceFixture fixture)
    {
        var recording = new Recording
        {
            Id = Guid.NewGuid(), Title = "Song", Cover = "",
            Release = new Release
            {
                Id = Guid.NewGuid(), Title = "Album", Cover = "",
                Artist = new Artist { Id = Guid.NewGuid(), Name = "Artist", SortName = "Artist" }
            }
        };
        fixture.Database.Recordings.Add(recording);
        await fixture.Database.SaveChangesAsync();
        return recording;
    }

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static SlskdSearchResult Search() => new()
        { State = "Completed", ResponseCount = 1, Responses = [new() { Username = Username, Files = [.. Tracks] }] };

    private static AddSlskdSourceRequest Request() => new()
    {
        SearchIdentifier = SearchIdentifier, Username = Username, DirectoryPath = DirectoryPath,
        Files = Tracks.Select(track => new ChangeSlskdSourceFile { Filename = track.Filename!, Size = track.Size }).ToList()
    };

    private sealed class ManualSourceFixture : IAsyncDisposable
    {
        private readonly DatabaseFixture _fixture;
        private readonly IServiceScope _scope;
        public string MusicRoot { get; } = Path.Combine(Path.GetTempPath(), "manual-source-" + Guid.NewGuid().ToString("N"));
        public Guid AlbumIdentifier { get; } = Guid.NewGuid();
        public TransferHandler Handler { get; } = new();
        public StreamingDbContext Database { get; }
        public DashboardController Controller { get; }

        private ManualSourceFixture(DatabaseFixture fixture)
        {
            _fixture = fixture;
            _scope = fixture.Services.CreateScope();
            Database = _scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            Directory.CreateDirectory(MusicRoot);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Music:Path"] = MusicRoot }).Build();
            Controller = new DashboardController(Database, null!, null!, configuration,
                NullLogger<DashboardController>.Instance, null!, new SlskdHelper(Handler), null!, null!, null!, null!);
        }

        public static async Task<ManualSourceFixture> CreateAsync(bool failed = false)
        {
            var fixture = new ManualSourceFixture(await DatabaseFixture.CreateAsync());
            fixture.Database.DownloadProviderConfigs.Add(new DownloadProviderConfig
            {
                ProviderName = "Soulseek", IsEnabled = true, ApiUrl = "http://slskd.test", ApiKey = "test-key"
            });
            fixture.Database.ReleasesToDownload.Add(new() { Id = fixture.AlbumIdentifier, Title = "Album", Artist = "Artist" });
            if (failed) fixture.Database.FailedDownloads.Add(new() { Id = fixture.AlbumIdentifier, Title = "Album", Artist = "Artist" });
            await fixture.Database.SaveChangesAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Handler.Dispose();
            _scope.Dispose();
            await _fixture.DisposeAsync();
            Directory.Delete(MusicRoot, recursive: true);
        }
    }

    private sealed class TransferHandler : HttpMessageHandler, IHttpClientFactory
    {
        private readonly HttpClient _client;
        private readonly List<SlskdTransferItem> _transfers = [];
        public List<List<SlskdFileDownloadRequest>> Submissions { get; } = [];
        public bool AcceptOnlyFirstTrack { get; set; }
        public bool SearchAvailable { get; set; } = true;
        public bool RejectSearch { get; set; }
        public string? Query { get; private set; }

        public TransferHandler() => _client = new HttpClient(this, disposeHandler: false);
        public HttpClient CreateClient(string name) => _client;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-API-Key")));
            if (request.Method == HttpMethod.Post && path.EndsWith("/searches"))
            {
                var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Query = payload.RootElement.GetProperty("searchText").GetString();
                return new HttpResponseMessage(RejectSearch ? HttpStatusCode.TooManyRequests : HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && path.Contains("/searches/"))
            {
                if (!SearchAvailable) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return Response(path.EndsWith("/responses") ? Search().Responses! : (object)Search());
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/downloads"))
                return Response(new List<SlskdTransferUser>
                    { new() { Username = Username, Directories = [new() { Directory = DirectoryPath, Files = _transfers }] } });
            Assert.Equal(HttpMethod.Post, request.Method);
            var files = JsonSerializer.Deserialize<List<SlskdFileDownloadRequest>>(
                await request.Content!.ReadAsStringAsync(cancellationToken), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Submissions.Add(files);
            foreach (var file in AcceptOnlyFirstTrack ? files.Take(1) : files)
                _transfers.Add(new() { Filename = file.Filename, Size = file.Size, State = "Queued, Remotely" });
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
