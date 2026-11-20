using System.Text.Json;
using StreamingServiceServer.Business.Lyrics;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsArtistPolicyTests
{
    [Fact]
    public void DuplicateCanonicalArtistCreditsCollapseAndAlbumArtistKeepsItsName()
    {
        var credits = LyricsArtistPolicy.Credits([LyricsTestData.Artist, LyricsRegressionData.SpacedUppercaseArtist]);
        Assert.Single(credits);
        Assert.Equal(LyricsTestData.Artist, LyricsArtistPolicy.Primary(LyricsTestData.Artist, credits));
        Assert.Null(LyricsArtistPolicy.Revision(credits));
    }

    [Fact]
    public void CreditedAlbumArtistIsPrimaryRegardlessOfCreditOrder()
    {
        var credits = LyricsArtistPolicy.Credits([LyricsTestData.OtherArtist, LyricsTestData.Artist]);
        Assert.Equal(LyricsTestData.Artist, LyricsArtistPolicy.Primary(LyricsTestData.Artist, credits));
        Assert.Equal(LyricsArtistPolicy.Revision(credits), LyricsArtistPolicy.Revision(LyricsArtistPolicy.Credits(credits.Reverse())));
    }

    [Fact]
    public void VariousArtistsAlbumUsesSoleRecordingArtist()
    {
        var credits = LyricsArtistPolicy.Credits([LyricsTestData.Artist]);
        Assert.Equal(LyricsTestData.Artist, LyricsArtistPolicy.Primary(LyricsMatchingTestData.VariousArtists, credits));
    }

    [Fact]
    public void UncreditedAlbumArtistCannotReplaceFullCollaboration()
    {
        var credits = LyricsArtistPolicy.Credits([LyricsTestData.Artist, LyricsTestData.OtherArtist]);
        Assert.Equal(LyricsMatchingTestData.Collaboration, LyricsArtistPolicy.Primary(LyricsMatchingTestData.VariousArtists, credits));
    }

    [Fact]
    public void EmptyCreditsAreRemovedBeforeSelectingSoleArtist()
    {
        var credits = LyricsArtistPolicy.Credits([null, string.Empty, LyricsTestData.Artist]);
        Assert.Single(credits);
        Assert.Equal(LyricsTestData.Artist, LyricsArtistPolicy.Primary(LyricsMatchingTestData.VariousArtists, credits));
    }

    [Fact]
    public void SingleArtistRevisionRetainsLegacySerializedIdentity()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            LyricsTestData.Track.RecordingIdentifier, LyricsTestData.Track.Title, LyricsTestData.Track.Artist,
            LyricsTestData.Track.Album, LyricsTestData.Track.DurationMilliseconds, LyricsTestData.Track.HasNamingIssue,
            LyricsTestData.Track.DurationSeconds
        });
        Assert.Equal(legacy, LyricsTestData.Track.Revision);
    }

    [Fact]
    public void FeaturedArtistChangesInvalidateRevisionEvenWhenPrimaryArtistUnchanged()
    {
        var single = LyricsTestData.Track;
        var collaboration = single with { ArtistCreditRevision = LyricsArtistPolicy.Revision(
            LyricsArtistPolicy.Credits([LyricsTestData.Artist, LyricsTestData.OtherArtist])) };
        Assert.NotEqual(single.Revision, collaboration.Revision);
        Assert.Equal(single.Artist, collaboration.Artist);
    }
}
