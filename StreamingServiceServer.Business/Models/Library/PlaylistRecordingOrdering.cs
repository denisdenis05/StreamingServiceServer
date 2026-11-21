using StreamingServiceServer.Data.Models.Library;

namespace StreamingServiceServer.Business.Models.Library;

public static class PlaylistRecordingOrdering
{
    private const int FirstPage = 1;
    private const int UnusedTiePosition = 0;

    public static IOrderedQueryable<PlaylistRecording> Apply(IQueryable<PlaylistRecording> recordings, bool newestFirst)
    {
        if (!newestFirst)
            return recordings.OrderBy(recording => recording.Order).ThenBy(recording => recording.RecordingId);

        return recordings
            .OrderByDescending(recording => recording.KnownAddedAt.HasValue)
            .ThenByDescending(recording => recording.KnownAddedAt)
            .ThenByDescending(recording => recording.KnownAddedAt.HasValue ? recording.Order : UnusedTiePosition)
            .ThenBy(recording => recording.Order)
            .ThenBy(recording => recording.RecordingId);
    }

    public static int PageOffset(int page, int pageSize) => checked((page - FirstPage) * pageSize);
}
