using System.Reflection;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceDownloader.Models.Soulseek;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AlbumQueueMatchingTests
{
    [Theory]
    [InlineData("The Doors", "The Doors", "Music/3 Doors Down/Away From The Sun", false)]
    [InlineData("Kendrick Lamar", "DAMN.", "Samples/Electric Mantis/EM_Misc", false)]
    [InlineData("The Doors", "The Doors", "Music/The Doors/The Doors (1967)", true)]
    [InlineData("Queen", "Hot Space", "Music/Queen/1982 Hot Space", true)]
    [InlineData("Billie Eilish", "HIT ME HARD AND SOFT", "Billie Eilish/HIT ME HARD AND SOFT THE TOUR LIVE", true)]
    public void SourceRequiresTheArtistAndAlbumRegardlessOfSearchQuery(string artist, string album, string path, bool accepted)
    {
        var method = typeof(SoulseekProvider).GetMethod("FindCandidateDirectoriesForFormat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var containsArtist in new[] { true, false })
        {
            var result = (List<CandidateDirectory>)method.Invoke(new SoulseekProvider(null!), [
                new ReleaseToDownload { Artist = artist, Title = album }, containsArtist,
                new List<SlskdSearchResponse> { new() { Username = "peer", FreeUploadSlots = 1, UploadSpeed = 2000000,
                    Files = [new() { Filename = path + "/01 song.flac", Size = 20000000 }] } },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".flac" }])!;
            Assert.Equal(accepted, result.Count > 0);
        }
    }

    [Fact]
    public void TrackNumberCannotOverrideContradictoryTagsOrTitle()
    {
        var expected = new RecordingResponse { Id = Guid.NewGuid(), ArtistName = "The Doors", ReleaseTitle = "The Doors", Title = "Soul Kitchen", PositionInAlbum = 2 };
        var files = new List<(string, string)> { ("02 - Away From The Sun.flac", "/probe.flac") };
        var match = Assert.Single(SoulseekMatchingHelper.MatchSongsWithRecordings([expected], files,
            _ => new AudioMetadata { Artist = "3 Doors Down", Album = "Away From The Sun", Title = "Away From The Sun" }));
        Assert.Equal(0, match.Score);
        Assert.Equal(0, Assert.Single(SoulseekMatchingHelper.MatchSongsByFilenameCombinations([expected], files)).Score);
    }

    [Theory]
    [InlineData("Artist", "Album", "Song", "Artist", "Album", "Song", true)]
    [InlineData("Charli xcx", "Brat", "360", "Charli xcx;Robyn;Yung Lean", "Brat", "360", true)]
    [InlineData("Artist", "Album", "Song", "Other Artist", "Album", "Song", false)]
    [InlineData("Artist", "Album", "Song", "Artist", "Other Album", "Song", false)]
    [InlineData("Artist", "Album", "Song", "Artist", "Album", "Song (live)", false)]
    [InlineData("MF DOOM", "Operation: Doomsday", "Rhymes Like Dimes", "MF Doom", "Operation: Doomsday", "Rhymes Like Dimes (feat. DJ Cucumber Slice)", true)]
    [InlineData("Artist", "Album", "Song", "Artist", "Album", "Song feat. Guest", true)]
    [InlineData("Artist", "Album", "Song", "Artist", "Album", "Song (remix)", false)]
    [InlineData("Artist", "Album", "?", "Artist", "Album", "?", true)]
    [InlineData("Artist", "Album", "?", "Artist", "Album", "_", false)]
    public void TaggedTracksRequireCompatibleIdentity(string artist, string album, string title,
        string actualArtist, string actualAlbum, string actualTitle, bool accepted)
    {
        var recording = new RecordingResponse { ArtistName = artist, ReleaseTitle = album, Title = title };
        Assert.Equal(accepted, AudioMatchPolicy.TrackScore(recording, "01.flac",
            new AudioMetadata { Artist = actualArtist, Album = actualAlbum, Title = actualTitle }) > 0);
    }

    [Fact]
    public void MusicBrainzCreditNamesAllowAnArtistRenameWithoutAcceptingOtherArtists()
    {
        var expected = new RecordingResponse { ArtistName = "Ye", ArtistAliases = ["Kanye West"], ReleaseTitle = "Donda", Title = "Donda Chant" };
        Assert.Equal(100, AudioMatchPolicy.TrackScore(expected, "01.flac",
            new AudioMetadata { Artist = "Kanye West", Album = "Donda", Title = "Donda Chant" }));
    }

    [Fact]
    public void AStandardEditionCannotSatisfyTheExpectedLargerTrackList()
    {
        var provider = new SoulseekProvider(null!);
        typeof(SoulseekProvider).GetField("_expectedTrackCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, 16);
        var method = typeof(SoulseekProvider).GetMethod("FindCandidateDirectoriesForFormat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var response = new SlskdSearchResponse { Username = "peer", FreeUploadSlots = 1, UploadSpeed = 2000000,
            Files = Enumerable.Range(1, 11).Select(number => new SlskdFile { Filename = $"Queen/Hot Space/{number:00} song.flac", Size = 20000000 }).ToList() };
        var result = (List<CandidateDirectory>)method.Invoke(provider, [new ReleaseToDownload { Artist = "Queen", Title = "Hot Space" },
            true, new List<SlskdSearchResponse> { response }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".flac" }])!;
        Assert.Empty(result);
    }

    [Fact]
    public async Task UnavailableAlbumMetadataIsRetryableRatherThanAnEmptySearchResult()
    {
        var metadata = DispatchProxy.Create<StreamingServiceServer.Business.Services.MusicSearch.IExternalMusicSearchService, UnavailableMetadata>();
        var provider = new SoulseekProvider(null!, metadata);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAndEnqueueAsync(
            new ReleaseToDownload { Id = Guid.NewGuid(), Artist = "Artist", Title = "Album" }, ["FLAC"], default));
    }

    public class UnavailableMetadata : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "SearchAlbumRecordingsByIdAsync" ? Task.FromResult(new List<RecordingDto>())
                : throw new InvalidOperationException("Unexpected metadata call.");
    }
}
