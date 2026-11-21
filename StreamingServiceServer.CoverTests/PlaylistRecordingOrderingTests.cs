using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class PlaylistRecordingOrderingTests
{
    private const int FirstPosition = 0;
    private const int SecondPosition = 1;
    private const int ThirdPosition = 2;
    private const int SecondPage = 2;
    private const int PageSize = 2;
    private const int ThirdPage = 3;
    private const int ThirdPageOffset = 4;
    private static readonly Guid FirstIdentifier = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondIdentifier = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdIdentifier = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly DateTime EarlierAddition = DateTime.Parse("2026-01-01T00:00:00Z").ToUniversalTime();
    private static readonly DateTime LaterAddition = DateTime.Parse("2026-02-01T00:00:00Z").ToUniversalTime();

    [Fact]
    public void LikedSongsWithoutAdditionDatesPreserveImportedPositions()
    {
        var first = Entry(FirstPosition);
        var second = Entry(SecondPosition);
        var third = Entry(ThirdPosition);
        Assert.Equal([first, second, third], Ordered(true, third, first, second));
    }

    [Fact]
    public void LikedSongsUseAdditionDatesBeforeSavedPositions()
    {
        var newest = Entry(FirstPosition, LaterAddition);
        var oldest = Entry(SecondPosition, EarlierAddition);
        var unknown = Entry(ThirdPosition);
        Assert.Equal([newest, oldest, unknown], Ordered(true, unknown, oldest, newest));
    }

    [Fact]
    public void OrdinaryPlaylistsPreservePositionsRegardlessOfAdditionDate()
    {
        var first = Entry(FirstPosition, EarlierAddition);
        var second = Entry(SecondPosition, LaterAddition);
        Assert.Equal([first, second], Ordered(false, second, first));
    }

    [Fact]
    public void AdditionsWithEqualDatesPutLastAddedPositionFirst()
    {
        var first = Entry(FirstPosition, LaterAddition);
        var second = Entry(SecondPosition, LaterAddition);
        Assert.Equal([second, first], Ordered(true, first, second));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateLegacyPositionsHaveStableIdentifierOrdering(bool newestFirst)
    {
        var first = Entry(FirstPosition);
        var second = Entry(FirstPosition);
        second.RecordingId = SecondIdentifier;
        var expected = new[] { first, second }.OrderBy(recording => recording.RecordingId).ToArray();
        Assert.Equal(expected, Ordered(newestFirst, first, second));
        Assert.Equal(expected, Ordered(newestFirst, second, first));
    }

    [Fact]
    public void OrderingHappensBeforePagination()
    {
        var first = Entry(FirstPosition);
        var second = Entry(SecondPosition);
        var third = Entry(ThirdPosition, LaterAddition);
        Assert.Equal([second], Ordered(true, second, first, third).Skip(PlaylistRecordingOrdering.PageOffset(SecondPage, PageSize)).Take(PageSize));
    }

    [Fact]
    public void DisplayOffsetCountsAllPreviousPages() =>
        Assert.Equal(ThirdPageOffset, PlaylistRecordingOrdering.PageOffset(ThirdPage, PageSize));

    [Fact]
    public void EmptyPlaylistsHaveNoDisplayEntries() => Assert.Empty(Ordered(true));

    private static PlaylistRecording Entry(int position, DateTime? addedAt = null) => new()
    {
        RecordingId = position switch
        {
            FirstPosition => FirstIdentifier,
            SecondPosition => SecondIdentifier,
            _ => ThirdIdentifier
        },
        Order = position, KnownAddedAt = addedAt
    };

    private static PlaylistRecording[] Ordered(bool newestFirst, params PlaylistRecording[] recordings) =>
        PlaylistRecordingOrdering.Apply(recordings.AsQueryable(), newestFirst).ToArray();
}
