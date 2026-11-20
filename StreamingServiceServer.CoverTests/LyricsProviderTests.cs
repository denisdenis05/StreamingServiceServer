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
    public async Task WrongTitleArtistAlbumOrDurationCannotDownloadLyrics(string title, string artist, string album, double durationSeconds)
    {
        using var fixture = new ProviderFixture { Body = LyricsTestData.Payload(title, artist, album, durationSeconds) };
        var failure = await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        Assert.Equal(LyricsProtocol.UnmatchedRecording, failure.FailureCode);
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
    public async Task HtmlAndOversizedResponsesAreNotAccepted()
    {
        using var fixture = new ProviderFixture { MediaType = LyricsTestData.WrongContentType };
        await Assert.ThrowsAsync<LyricsProviderException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
        fixture.MediaType = LyricsProtocol.JsonMediaType;
        fixture.Body = new string(LyricsTestData.OversizedResponseCharacter, LyricsProtocol.MaximumResponseBytes + LyricsTestData.OneCall);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.DownloadAsync(LyricsTestData.Track, CancellationToken.None));
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

    private sealed class ProviderFixture : HttpMessageHandler
    {
        public string Body { get; set; } = LyricsTestData.Payload();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string MediaType { get; set; } = LyricsProtocol.JsonMediaType;
        public RetryConditionHeaderValue? RetryAfter { get; set; }
        public TestClock Clock { get; } = new();
        public string Address { get; private set; } = string.Empty;
        public bool HadAuthorization { get; private set; }
        public bool HadProviderKey { get; private set; }
        public int Calls { get; private set; }
        private readonly HttpClient client;
        public ILyricsProvider Provider { get; }
        public ProviderFixture()
        {
            client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(LyricsTestData.ResponseAddress) };
            Provider = new LrclibLyricsProvider(client, Clock);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls += LyricsTestData.OneCall;
            Address = request.RequestUri!.AbsoluteUri;
            HadAuthorization = request.Headers.Contains(LyricsTestData.AuthorizationHeader);
            HadProviderKey = request.Headers.Contains(LyricsTestData.ProviderKeyHeader);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, MediaType) };
            response.Headers.RetryAfter = RetryAfter;
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
}
