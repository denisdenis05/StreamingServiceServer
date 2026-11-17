using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class SoulseekTrackSearchFallbackTests
{
    private static readonly Guid CleanupSearchIdentifier = Guid.Parse("a5dd5376-e7cd-4c51-b017-fce445a3deba");
    private const string Artist = "Test Artist";
    private const string Album = "Test Album";
    private const string Username = "album-owner";
    private const string DirectoryPath = "Music/Test Artist/Test Album";
    private const string OtherDirectoryPath = "Music/Other Artist/Other Album";
    private const string FirstTitle = "Opening Song";
    private const string SecondTitle = "Middle Song";
    private const string ThirdTitle = "Closing Song";
    private const string FourthTitle = "Bonus Song";
    private const string ExtraTitle = "Unwanted Track";
    private const string LosslessFormat = "FLAC";
    private const string LossyFormat = "MP3";
    private const string LosslessExtension = ".flac";
    private const string LossyExtension = ".mp3";
    private const string PathSeparator = "/";
    private const long TrackSizeBytes = 20_000_000;
    private const int AvailableUploadSlots = 1;
    private const int FirstTrackPosition = 1;
    private const int SecondTrackPosition = 2;
    private const int ThirdTrackPosition = 3;
    private const int NormalSearchQueryCount = 2;
    private static readonly ReleaseToDownload Release = new() { Artist = Artist, Title = Album };
    private static readonly string[] AlbumTitles = [FirstTitle, SecondTitle, ThirdTitle, FourthTitle];

    [Fact]
    public async Task EmptyAlbumSearchesExpandFirstTrackAndEnqueueOnlyRequiredTracks()
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.DirectoryFiles = [.. AlbumTitles.Select(title => File(title)), File(ExtraTitle)];
        var result = await SearchAsync(client);
        Assert.True(result?.Success);
        Assert.Equal(new[] { Artist + " " + Album, Album, FirstTitle }, client.Queries);
        Assert.Equal(AlbumTitles.Select(title => File(title).Filename), Assert.Single(client.Enqueued).Files.Select(file => file.Filename));
        var source = SlskdSourceIdentity.Parse(result!.SourceIdentifier!);
        Assert.Equal(Assert.Single(client.Enqueued).SearchId, source!.SearchId);
        Assert.Equal(Assert.Single(client.Enqueued).Files.Select(file => file.Filename), source.Files.Select(file => file.Filename));
        Assert.DoesNotContain(source!.SearchId, client.DeletedSearches);
        Assert.Equal(client.Queries.Count, client.SearchSlotRequests);
    }

    [Theory]
    [InlineData(SecondTitle)]
    [InlineData(ThirdTitle)]
    public async Task UnhelpfulTracksAdvanceToNextTrackUntilSuccessful(string successfulTitle)
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle, OtherDirectoryPath)];
        client.SearchFiles[successfulTitle] = [File(successfulTitle)];
        client.DirectoryFiles = AlbumTitles.Select(title => File(title)).ToList();
        Assert.NotNull(await SearchAsync(client));
        Assert.Equal(successfulTitle, client.Queries.Last());
        Assert.Equal(DirectoryPath, Assert.Single(client.DirectoryRequests));
        Assert.Single(client.Enqueued);
        Assert.Equal(client.Queries.Count - AvailableUploadSlots, client.DeletedSearches.Count);
    }

    [Fact]
    public async Task OnlyFirstThreeTracksAreSearchedEvenWhenFourthHasResults()
    {
        var client = new SearchClient();
        client.SearchFiles[FourthTitle] = [File(FourthTitle)];
        Assert.Null(await SearchAsync(client));
        Assert.Equal(new[] { Artist + " " + Album, Album, FirstTitle, SecondTitle, ThirdTitle }, client.Queries);
        Assert.Empty(client.Enqueued);
        Assert.Equal(client.Queries.Count, client.DeletedSearches.Count);
    }

    [Theory]
    [InlineData(FirstTrackPosition)]
    [InlineData(SecondTrackPosition)]
    public async Task ShortReleasesSearchOnlyTheirAvailableTracks(int trackCount)
    {
        var client = new SearchClient();
        var titles = AlbumTitles.Take(trackCount).ToArray();
        Assert.Null(await SearchAsync(client, titles));
        Assert.Equal(titles, client.Queries.Skip(NormalSearchQueryCount));
    }

    [Fact]
    public async Task SingleTrackReleaseCanExpandAndDownload()
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.DirectoryFiles = [File(FirstTitle), File(ExtraTitle)];
        Assert.NotNull(await SearchAsync(client, [FirstTitle]));
        Assert.Equal(File(FirstTitle).Filename, Assert.Single(Assert.Single(client.Enqueued).Files).Filename);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NormalResultsNeverTriggerTrackFallback(bool matchesAlbum)
    {
        var client = new SearchClient();
        client.SearchFiles[Artist + " " + Album] = AlbumTitles.Select(title => File(title,
            matchesAlbum ? DirectoryPath : OtherDirectoryPath)).ToList();
        var result = await SearchAsync(client);
        Assert.Equal(matchesAlbum, result is not null);
        Assert.DoesNotContain(FirstTitle, client.Queries);
        Assert.Empty(client.DirectoryRequests);
    }

    [Fact]
    public async Task ResponsesWithoutFilesStillTriggerFallback()
    {
        var client = new SearchClient { ReturnEmptyPeerResponses = true };
        Assert.Null(await SearchAsync(client, [FirstTitle]));
        Assert.Contains(FirstTitle, client.Queries);
    }

    [Fact]
    public async Task DuplicateAndBlankSeedTitlesDoNotCauseRepeatedOrExtraTrackSearches()
    {
        var client = new SearchClient();
        Assert.Null(await SearchAsync(client, [FirstTitle, FirstTitle, string.Empty, FourthTitle]));
        Assert.Equal(new[] { Artist + " " + Album, Album, FirstTitle }, client.Queries);
    }

    [Fact]
    public async Task TrackPositionDeterminesSeedOrder()
    {
        var client = new SearchClient();
        var recordings = new List<RecordingDto>
        {
            new() { Title = ThirdTitle, PositionInAlbum = ThirdTrackPosition },
            new() { Title = FirstTitle, PositionInAlbum = FirstTrackPosition },
            new() { Title = SecondTitle, PositionInAlbum = SecondTrackPosition }
        };
        Assert.Null(await CreateProvider(client, recordings).SearchAndEnqueueAsync(Release, [LosslessFormat], default));
        Assert.Equal(new[] { FirstTitle, SecondTitle, ThirdTitle }, client.Queries.Skip(NormalSearchQueryCount));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteOrWrongTrackDirectoriesCannotSatisfyAlbum(bool missingTrack)
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.DirectoryFiles = missingTrack ? [File(FirstTitle)]
            : [File(FirstTitle), File(SecondTitle), File(ThirdTitle), File(ExtraTitle)];
        Assert.Null(await SearchAsync(client));
        Assert.Empty(client.Enqueued);
        Assert.Contains(ThirdTitle, client.Queries);
    }

    [Fact]
    public async Task LockedOrDisallowedFilesCannotCompleteAlbum()
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        var lockedFile = File(SecondTitle);
        lockedFile.IsLocked = true;
        client.DirectoryFiles = [File(FirstTitle), lockedFile, File(ThirdTitle), File(FourthTitle, extension: LossyExtension)];
        Assert.Null(await SearchAsync(client));
        Assert.Empty(client.Enqueued);
    }

    [Fact]
    public async Task FormatPreferenceIsPreservedWhenDirectoryHasMultipleEncodings()
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle), File(FirstTitle, extension: LossyExtension)];
        client.DirectoryFiles = AlbumTitles.SelectMany(title => new[] { File(title), File(title, extension: LossyExtension) }).ToList();
        Assert.NotNull(await SearchAsync(client, formats: [LossyFormat, LosslessFormat]));
        Assert.All(Assert.Single(client.Enqueued).Files, file => Assert.EndsWith(LossyExtension, file.Filename));
        Assert.Single(client.DirectoryRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectoryOrEnqueueFailureAdvancesToNextSeed(bool directoryFails)
    {
        var client = new SearchClient { FailFirstDirectory = directoryFails, FailFirstEnqueue = !directoryFails };
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.SearchFiles[SecondTitle] = [File(SecondTitle)];
        client.DirectoryFiles = AlbumTitles.Select(title => File(title)).ToList();
        Assert.NotNull(await SearchAsync(client));
        Assert.Equal(SecondTitle, client.Queries.Last());
        Assert.Single(client.Enqueued);
    }

    [Fact]
    public async Task CancellationDuringExpansionStopsLaterSearchesAndCleansSeedSearch()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new SearchClient { CancelDirectory = cancellation };
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchAsync(client, cancellationToken: cancellation.Token));
        Assert.Equal(FirstTitle, client.Queries.Last());
        Assert.Equal(client.Queries.Count, client.DeletedSearches.Count);
        Assert.Empty(client.Enqueued);
    }

    [Fact]
    public async Task MissingMetadataDoesNotStartAnySearch()
    {
        var client = new SearchClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => SearchAsync(client, []));
        Assert.Empty(client.Queries);
    }

    [Fact]
    public async Task DirectoryEndpointEscapesPeerAndScopesRelativeAndFullFilenames()
    {
        const string peerWithReservedCharacters = "peer/name #";
        using var endpoint = new DirectoryEndpoint();
        var helper = new SlskdHelper(endpoint);
        var files = await helper.GetDirectoryFilesAsync(peerWithReservedCharacters, DirectoryPath, default);
        Assert.Equal(Uri.EscapeDataString(peerWithReservedCharacters), endpoint.EncodedUsername);
        Assert.Equal(DirectoryPath, endpoint.RequestedDirectory);
        Assert.Equal(new[] { File(FirstTitle).Filename, File(SecondTitle).Filename },
            files.Select(file => SlskdSourceIdentity.NormalizePath(file.Filename!)));
        Assert.All(files, file => Assert.Equal(TrackSizeBytes, file.Size));
    }

    [Fact]
    public async Task DirectoryHttpFailureIsReportedToFallback()
    {
        using var endpoint = new DirectoryEndpoint { Status = HttpStatusCode.NotFound };
        await Assert.ThrowsAsync<HttpRequestException>(() => new SlskdHelper(endpoint)
            .GetDirectoryFilesAsync(Username, DirectoryPath, default));
    }

    [Fact]
    public async Task UnavailableSearchResultsDoNotCountAsAnEmptySearch()
    {
        var client = new SearchClient { SearchResultsUnavailable = true };
        Assert.Null(await SearchAsync(client));
        Assert.DoesNotContain(FirstTitle, client.Queries);
    }

    [Fact]
    public async Task FailedPreferredFormatEnqueueTriesNextFormatFromSameDirectory()
    {
        var client = new SearchClient { FailFirstEnqueue = true };
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.DirectoryFiles = AlbumTitles.SelectMany(title => new[] { File(title), File(title, extension: LossyExtension) }).ToList();
        Assert.NotNull(await SearchAsync(client, formats: [LosslessFormat, LossyFormat]));
        Assert.Equal(FirstTitle, client.Queries.Last());
        Assert.All(Assert.Single(client.Enqueued).Files, file => Assert.EndsWith(LossyExtension, file.Filename));
    }

    [Fact]
    public async Task OneRemoteFileCannotSatisfyTwoRecordingsWithTheSameTitle()
    {
        var client = new SearchClient();
        client.SearchFiles[FirstTitle] = [File(FirstTitle)];
        client.DirectoryFiles = [File(FirstTitle), File(ExtraTitle)];
        Assert.Null(await SearchAsync(client, [FirstTitle, FirstTitle]));
        Assert.Empty(client.Enqueued);
    }

    [Fact]
    public async Task SearchCleanupSendsDeleteRequestToSlskd()
    {
        using var endpoint = new DirectoryEndpoint();
        var helper = new SlskdHelper(endpoint);
        await helper.DeleteSearchAsync(CleanupSearchIdentifier);
        Assert.True(endpoint.SearchDeleted);
    }

    [Fact]
    public async Task SearchCleanupHttpFailureDoesNotReplaceTheSearchOutcome()
    {
        using var endpoint = new DirectoryEndpoint { Status = HttpStatusCode.ServiceUnavailable };
        await new SlskdHelper(endpoint).DeleteSearchAsync(CleanupSearchIdentifier);
        Assert.True(endpoint.SearchDeleted);
    }

    [Fact]
    public async Task SearchCleanupPreservesCallerCancellation()
    {
        using var endpoint = new DirectoryEndpoint();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SlskdHelper(endpoint)
            .DeleteSearchAsync(CleanupSearchIdentifier, cancellation.Token));
    }

    private static Task<StreamingServiceDownloader.Models.DownloadSearchResult?> SearchAsync(SearchClient client,
        string[]? titles = null, string[]? formats = null, CancellationToken cancellationToken = default) =>
        CreateProvider(client, (titles ?? AlbumTitles).Select((title, position) => new RecordingDto
            { Title = title, PositionInAlbum = position + FirstTrackPosition }).ToList())
            .SearchAndEnqueueAsync(Release, formats ?? [LosslessFormat], cancellationToken);

    private static SoulseekProvider CreateProvider(SearchClient client, List<RecordingDto> recordings)
    {
        var metadata = DispatchProxy.Create<IExternalMusicSearchService, MetadataSource>();
        ((MetadataSource)(object)metadata).Recordings = recordings;
        return new SoulseekProvider(null!, metadata, client, cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.SearchSlotRequests++;
            return Task.CompletedTask;
        });
    }

    private static SlskdFile File(string title, string directory = DirectoryPath, string extension = LosslessExtension) =>
        new() { Filename = directory + PathSeparator + title + extension, Size = TrackSizeBytes };

    public class MetadataSource : DispatchProxy
    {
        public List<RecordingDto> Recordings { get; set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments) =>
            targetMethod?.Name == nameof(IExternalMusicSearchService.SearchAlbumRecordingsByIdAsync)
                ? Task.FromResult(Recordings) : throw new InvalidOperationException();
    }

    private sealed class SearchClient : ISoulseekAlbumSearchClient
    {
        public Dictionary<string, List<SlskdFile>> SearchFiles { get; } = [];
        public List<string> Queries { get; } = [];
        public List<string> DirectoryRequests { get; } = [];
        public List<Guid> DeletedSearches { get; } = [];
        public List<SlskdSourceIdentity> Enqueued { get; } = [];
        public List<SlskdFile> DirectoryFiles { get; set; } = [];
        public bool SearchResultsUnavailable { get; set; }
        public bool ReturnEmptyPeerResponses { get; set; }
        public bool FailFirstDirectory { get; set; }
        public bool FailFirstEnqueue { get; set; }
        public CancellationTokenSource? CancelDirectory { get; set; }
        public int SearchSlotRequests { get; set; }
        private const int GuidSequenceOffset = 0;
        private const int NoFiles = 0;
        private readonly Dictionary<Guid, string> _searches = [];

        public Task<Guid?> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            var identifierBytes = Guid.Empty.ToByteArray();
            BitConverter.GetBytes(Queries.Count).CopyTo(identifierBytes, GuidSequenceOffset);
            var searchIdentifier = new Guid(identifierBytes);
            _searches[searchIdentifier] = query;
            return Task.FromResult<Guid?>(searchIdentifier);
        }

        public Task<SlskdSearchResult?> WaitForSearchResultsAsync(Guid searchIdentifier, int maximumWaitSeconds,
            CancellationToken cancellationToken = default)
        {
            if (SearchResultsUnavailable) return Task.FromResult<SlskdSearchResult?>(null);
            var files = SearchFiles.GetValueOrDefault(_searches[searchIdentifier]) ?? [];
            return Task.FromResult<SlskdSearchResult?>(new()
            {
                Responses = files.Count > NoFiles || ReturnEmptyPeerResponses
                    ? [new() { Username = Username, Files = files, FreeUploadSlots = AvailableUploadSlots }] : []
            });
        }

        public Task<List<SlskdFile>> GetDirectoryFilesAsync(string username, string directoryPath, CancellationToken cancellationToken)
        {
            Assert.Equal(Username, username);
            DirectoryRequests.Add(directoryPath);
            CancelDirectory?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (FailFirstDirectory)
            {
                FailFirstDirectory = false;
                throw new HttpRequestException();
            }
            return Task.FromResult(DirectoryFiles);
        }

        public Task StartAlbumDownloadAsync(SlskdSourceIdentity source, CancellationToken cancellationToken)
        {
            if (FailFirstEnqueue)
            {
                FailFirstEnqueue = false;
                throw new HttpRequestException();
            }
            Enqueued.Add(source);
            return Task.CompletedTask;
        }

        public Task DeleteSearchAsync(Guid searchIdentifier, CancellationToken cancellationToken = default)
        {
            DeletedSearches.Add(searchIdentifier);
            return Task.CompletedTask;
        }
    }

    private sealed class DirectoryEndpoint : HttpMessageHandler, IHttpClientFactory
    {
        private const string JsonMediaType = "application/json";
        private const string DirectoryProperty = "directory";
        private const int UsernameSegment = 4;
        public bool SearchDeleted { get; private set; }
        public string? EncodedUsername { get; private set; }
        public string? RequestedDirectory { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Delete)
            {
                SearchDeleted = true;
                return new HttpResponseMessage(Status);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            EncodedUsername = request.RequestUri!.AbsolutePath.Split('/')[UsernameSegment];
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedDirectory = payload.RootElement.GetProperty(DirectoryProperty).GetString();
            var relativeFile = File(FirstTitle);
            relativeFile.Filename = FirstTitle + LosslessExtension;
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[]
                {
                    new SlskdDirectory { Name = DirectoryPath, Files = [relativeFile, File(SecondTitle), File(ThirdTitle, OtherDirectoryPath)] },
                    new SlskdDirectory { Name = OtherDirectoryPath, Files = [File(FourthTitle)] }
                }), Encoding.UTF8, JsonMediaType)
            };
        }
    }
}
