using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Lyrics;

public sealed record LyricsResponse(Guid RecordingIdentifier, string State, string Provider, long? ProviderIdentifier,
    string? PlainLyrics, string? SyncedLyrics, string? Lyricsfile, DateTimeOffset? UpdatedAt, string? Reason);

public sealed class LyricsReader(StreamingDbContext database, ILyricsCatalog catalog)
{
    public async Task<LyricsResponse?> ReadAsync(Guid recordingIdentifier, CancellationToken cancellationToken)
    {
        var track = await catalog.ReadAsync(recordingIdentifier, cancellationToken);
        if (track is null) return null;
        var stored = await database.RecordingLyrics.AsNoTracking().SingleOrDefaultAsync(lyrics => lyrics.RecordingIdentifier == recordingIdentifier,
            cancellationToken);
        var rejection = LyricsMatchPolicy.Rejection(track);
        if (rejection is not null) return Empty(recordingIdentifier, LyricsDownloadState.Skipped, rejection);
        if (stored is null || stored.SourceRevision != track.Revision) return Empty(recordingIdentifier, LyricsDownloadState.Pending, null);
        var available = stored.State is LyricsDownloadState.Downloaded or LyricsDownloadState.Instrumental;
        return new LyricsResponse(recordingIdentifier, stored.State.ToString(), LyricsProtocol.Provider,
            available ? stored.ProviderIdentifier : null, available ? stored.PlainLyrics : null, available ? stored.SyncedLyrics : null,
            available ? stored.Lyricsfile : null, available ? stored.UpdatedAt : null,
            string.IsNullOrEmpty(stored.LastFailure) ? null : stored.LastFailure);
    }

    private static LyricsResponse Empty(Guid recordingIdentifier, LyricsDownloadState state, string? reason) =>
        new(recordingIdentifier, state.ToString(), LyricsProtocol.Provider, null, null, null, null, null, reason);
}
