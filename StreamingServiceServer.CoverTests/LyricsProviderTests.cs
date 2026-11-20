using System.Net;
using System.Net.Http.Headers;
using System.Text;
using StreamingServiceServer.Business.Lyrics;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsProviderTests
{
    [Fact]
    public async Task AnonymousLookupEncodesMetadataAndPreservesAllDownloadedFormats()
    {
        using var fixture = new ProviderFixture();
        var content = await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None);
        Assert.Equal(LyricsTestData.Content, content);
        Assert.Contains(LyricsTestData.ExpectedEscapedTitle, fixture.Address);
        Assert.Contains(LyricsTestData.ExpectedDuration, fixture.Address);
        Assert.False(fixture.HadAuthorization);
        Assert.False(fixture.HadProviderKey);
    }

    [Theory]
    [InlineData(LyricsTestData.OtherTitle, LyricsTestData.Artist, LyricsTestData.Album, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.OtherArtist, LyricsTestData.Album, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.Artist, LyricsTestData.OtherAlbum, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.Artist, LyricsTestData.Album, LyricsTestData.DifferentDurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.Artist, LyricsTestData.Album, LyricsTestData.OutsideDurationSeconds)]
    public async Task MismatchedExactResponseRequiresSearchConfirmation(string title, string artist, string album, double durationSeconds)
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.Payload(title, artist, album, durationSeconds) };
        Assert.Null(await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.TwoCalls, fixture.Calls);
    }

    [Fact]
    public async Task CaseWhitespaceAndDurationBoundaryAreAcceptedWithoutRemovingVersionLabels()
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.Payload(LyricsTestData.NameWithWhitespace.ToUpperInvariant(),
            durationSeconds: LyricsTestData.BoundaryDurationSeconds) };
        Assert.NotNull(await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Fact]
    public async Task MissingResultIsNotTreatedAsAProviderFailure()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound };
        Assert.Null(await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Fact]
    public async Task InstrumentalTracksAreStoredWithoutLyrics()
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.InstrumentalJson };
        var content = await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None);
        Assert.True(content!.Instrumental);
        Assert.Null(content.PlainLyrics);
        Assert.Null(content.SyncedLyrics);
    }

    [Fact]
    public async Task EmptyLyricsAreRejectedAsAnInvalidResponse()
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.Payload(plain: null, synced: null) };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsProtocol.InvalidResponse, failure.FailureCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProviderCooldownIsReadBeforeParsingAnErrorBody(HttpStatusCode status)
    {
        using var fixture = new ProviderFixture { Status = status, RetryAfter = new RetryConditionHeaderValue(LyricsTestData.ProviderCooldown),
            Body = LyricsTestData.InvalidJson, MediaType = LyricsTestData.WrongContentType };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.ProviderCooldown + LyricsTestData.RetrySafetyMargin, failure.RetryAfter);
    }

    [Fact]
    public async Task ProviderCooldownSupportsAnHttpDate()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.TooManyRequests };
        fixture.RetryAfter = new RetryConditionHeaderValue(fixture.Clock.GetUtcNow() + LyricsTestData.ProviderCooldown);
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.ProviderCooldown + LyricsTestData.RetrySafetyMargin, failure.RetryAfter);
    }

    [Fact]
    public async Task RateLimitWithoutRetryHeaderStillRequestsASharedCooldown()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.TooManyRequests };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.True(failure.RetryAfter > new LyricsOptions().MinimumRequestInterval);
        Assert.Equal(LyricsTestData.OneCall, fixture.Calls);
    }

    [Fact]
    public async Task HtmlAndOversizedResponsesAreNotAccepted()
    {
        using var fixture = new ProviderFixture { MediaType = LyricsTestData.WrongContentType };
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        fixture.MediaType = LyricsProtocol.JsonMediaType;
        fixture.Body = new string(LyricsTestData.OversizedResponseCharacter, LyricsProtocol.MaximumResponseBytes + LyricsTestData.OneCall);
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsProtocol.InvalidResponse, failure.FailureCode);
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public async Task FlaggedTracksAndMissingMetadataNeverReachTheNetwork()
    {
        using var fixture = new ProviderFixture();
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track with { HasNamingIssue = true }, CancellationToken.None));
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track with { Artist = string.Empty }, CancellationToken.None));
        Assert.Equal(LyricsTestData.NoCalls, fixture.Calls);
    }

    [Theory]
    [InlineData(LyricsTestData.NameMismatch, true)]
    [InlineData(LyricsTestData.ArtistMismatch, true)]
    [InlineData(LyricsTestData.LengthMismatch, false)]
    public void DashboardFlagsSkipNameAndArtistMismatchesOnly(string issueType, bool expected)
    {
        Assert.Equal(expected, LyricsCatalog.HasNamingIssue(LyricsTestData.Issues(issueType), LyricsTestData.RecordingIdentifier));
        Assert.False(LyricsCatalog.HasNamingIssue(LyricsTestData.Issues(issueType, LyricsTestData.OtherRecordingIdentifier), LyricsTestData.RecordingIdentifier));
    }

    [Fact]
    public void UnreadableMismatchFlagsFailClosedAndEmptyFlagsAllowLookup()
    {
        Assert.True(LyricsCatalog.HasNamingIssue(LyricsTestData.InvalidJson, LyricsTestData.RecordingIdentifier));
        Assert.False(LyricsCatalog.HasNamingIssue(LyricsTestData.EmptyIssues, LyricsTestData.RecordingIdentifier));
    }

    [Fact]
    public async Task MissingExactResultSearchesAndAcceptsAnUnambiguousAlternateAlbum()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(album: LyricsTestData.OtherAlbum)) };
        Assert.Equal(LyricsTestData.Content, await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.TwoCalls, fixture.Calls);
        Assert.Contains(LyricsMatchingTestData.SearchRoute, fixture.Address);
        Assert.Equal(LyricsTestData.OneCall, fixture.DelayCalls);
    }

    [Fact]
    public async Task MatchingExactResultNeverSearchesOrWaits()
    {
        using var fixture = new ProviderFixture();
        Assert.NotNull(await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.OneCall, fixture.Calls);
        Assert.Equal(LyricsTestData.NoCalls, fixture.DelayCalls);
    }

    [Fact]
    public async Task SearchPrefersMatchingAlbumOverConflictingOtherAlbum()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound, SearchBody = Search(
            LyricsTestData.Payload(album: LyricsTestData.OtherAlbum, plain: LyricsMatchingTestData.ConflictingPlain), LyricsTestData.Payload()) };
        Assert.Equal(LyricsTestData.Content, await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Theory]
    [InlineData(LyricsMatchingTestData.ConflictingPlain, LyricsTestData.Synced)]
    [InlineData(LyricsTestData.Plain, LyricsMatchingTestData.ConflictingSynced)]
    public async Task ConflictingViableSearchLyricsAreRejected(string plain, string synced)
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(), LyricsTestData.Payload(plain: plain, synced: synced)) };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsProtocol.AmbiguousRecording, failure.FailureCode);
    }

    [Fact]
    public async Task ConflictingInstrumentalClassificationIsRejected()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(), LyricsTestData.InstrumentalJson) };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsProtocol.AmbiguousRecording, failure.FailureCode);
    }

    [Fact]
    public async Task EquivalentPlainAndSyncedCandidatesPreferAvailableTiming()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(synced: null), LyricsTestData.Payload()) };
        Assert.Equal(LyricsTestData.Content, await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Fact]
    public async Task SearchFindsInstrumentalRecordsOnAlternateAlbum()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound, SearchBody = Search(LyricsTestData.InstrumentalJson) };
        Assert.True((await fixture.Provider.DownloadAsync(LyricsTestData.Track with { Album = LyricsTestData.OtherAlbum }, CancellationToken.None))!.Instrumental);
    }

    [Theory]
    [InlineData(LyricsTestData.OtherTitle, LyricsTestData.Artist, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.OtherArtist, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsMatchingTestData.LiveTitle, LyricsTestData.Artist, LyricsTestData.DurationSeconds)]
    [InlineData(LyricsTestData.Title, LyricsTestData.Artist, LyricsTestData.OutsideDurationSeconds)]
    public async Task SearchNeverRelaxesArtistTitleVersionOrDuration(string title, string artist, double duration)
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(title, artist, durationSeconds: duration)) };
        Assert.Null(await fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Theory]
    [InlineData(LyricsTestData.Album, LyricsMatchingTestData.LiveAlbum)]
    [InlineData(LyricsMatchingTestData.LiveAlbum, LyricsTestData.Album)]
    [InlineData(LyricsTestData.Album, LyricsMatchingTestData.RemixAlbum)]
    public async Task SearchDoesNotCrossPerformanceVersionAlbumLabels(string album, string candidateAlbum)
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound, SearchBody = Search(LyricsTestData.Payload(album: candidateAlbum)) };
        Assert.Null(await fixture.Provider.DownloadAsync(LyricsTestData.Track with { Album = album }, CancellationToken.None));
    }

    [Fact]
    public async Task TypographicApostrophesMatchStraightApostrophesAndSearchUsesStraightForm()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(title: LyricsMatchingTestData.StraightApostropheTitle)) };
        Assert.NotNull(await fixture.Provider.DownloadAsync(LyricsTestData.Track with { Title = LyricsMatchingTestData.ApostropheTitle }, CancellationToken.None));
        Assert.DoesNotContain(Uri.EscapeDataString(LyricsMatchingTestData.ApostropheTitle), fixture.Address);
        Assert.Contains(Uri.EscapeDataString(LyricsMatchingTestData.StraightApostropheTitle), fixture.Address);
    }

    [Theory]
    [InlineData(LyricsMatchingTestData.UnknownArtist)]
    [InlineData(LyricsMatchingTestData.VariousArtists)]
    public async Task UnknownArtistMetadataNeverContactsProvider(string artist)
    {
        using var fixture = new ProviderFixture();
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track with { Artist = artist }, CancellationToken.None));
        Assert.Equal(LyricsTestData.NoCalls, fixture.Calls);
    }

    [Fact]
    public async Task FeaturedArtistAloneCannotReplaceTheKnownPrimaryArtist()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound,
            SearchBody = Search(LyricsTestData.Payload(artist: LyricsTestData.OtherArtist)) };
        var collaboration = LyricsTestData.Track with { ArtistCreditRevision = LyricsArtistPolicy.Revision(
            LyricsArtistPolicy.Credits([LyricsTestData.Artist, LyricsTestData.OtherArtist])) };
        Assert.Null(await fixture.Provider.DownloadAsync(collaboration, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExactProviderFailureNeverTriggersSearch(HttpStatusCode status)
    {
        using var fixture = new ProviderFixture { Status = status };
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.OneCall, fixture.Calls);
        Assert.Equal(LyricsTestData.NoCalls, fixture.DelayCalls);
    }

    [Fact]
    public async Task InvalidExactJsonNeverTriggersSearch()
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.InvalidJson };
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsTestData.OneCall, fixture.Calls);
    }

    [Fact]
    public async Task NullSearchEntriesAndOversizedCandidateListsAreRejected()
    {
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound, SearchBody = LyricsMatchingTestData.MalformedSearch };
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        fixture.SearchBody = Search(Enumerable.Repeat(LyricsTestData.Payload(), LyricsProtocol.MaximumProviderSearchResults + LyricsTestData.OneCall).ToArray());
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringRequestSpacingPreventsSearch()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProviderFixture { Status = HttpStatusCode.NotFound, OnDelay = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, cancellation.Token));
        Assert.Equal(LyricsTestData.OneCall, fixture.Calls);
    }

    private static string Search(params string[] records) => System.Text.Json.JsonSerializer.Serialize(
        records.Select(record => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(record)));

    private sealed class ProviderFixture : HttpMessageHandler, ILyricsRequestDelay
    {
        public string Body { get; set; } = LyricsTestData.Payload();
        public string SearchBody { get; set; } = LyricsMatchingTestData.EmptySearch;
        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string MediaType { get; set; } = LyricsProtocol.JsonMediaType;
        public RetryConditionHeaderValue? RetryAfter { get; set; }
        public TestClock Clock { get; } = new();
        public string Address { get; private set; } = string.Empty;
        public bool HadAuthorization { get; private set; }
        public bool HadProviderKey { get; private set; }
        public int Calls { get; private set; }
        public int DelayCalls { get; private set; }
        public Action? OnDelay { get; set; }
        private readonly HttpClient client;
        public ILyricsProvider Provider { get; }
        public ProviderFixture()
        {
            client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(LyricsTestData.ResponseAddress) };
            Provider = new LrclibLyricsProvider(client, Clock, this);
        }
        public Task WaitAsync(CancellationToken cancellationToken)
        {
            DelayCalls += LyricsTestData.OneCall;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls += LyricsTestData.OneCall;
            Address = request.RequestUri!.AbsoluteUri;
            HadAuthorization = request.Headers.Contains(LyricsTestData.AuthorizationHeader);
            HadProviderKey = request.Headers.Contains(LyricsTestData.ProviderKeyHeader);
            var searching = request.RequestUri.AbsolutePath.EndsWith(LyricsProtocol.SearchRoute, StringComparison.Ordinal);
            var response = new HttpResponseMessage(searching ? SearchStatus : Status)
                { Content = new StringContent(searching ? SearchBody : Body, Encoding.UTF8, MediaType) };
            response.Headers.RetryAfter = RetryAfter;
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
}
