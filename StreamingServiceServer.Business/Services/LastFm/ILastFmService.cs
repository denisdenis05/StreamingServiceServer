using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.LastFm;

public interface ILastFmService
{
    public Task<string> GenerateAuthUrl();
    Task<(string SessionKey, string Username)> GetSessionKeyAndUsername(string token);
    Task<bool> ScrobbleTrack(string sessionKey, string artist, string track, string album, long timestamp);
    Task<bool> UpdateNowPlaying(string sessionKey, string artist, string track, string album, int duration);
    Task<Guid> StartPlaybackSession(Guid userId, StartPlaybackRequest request);
    Task RecordPlaybackEvent(Guid sessionId, PlaybackEvent playbackEvent);
    Task StopPlaybackSession(Guid sessionId, int clientReportedSeconds);
    Task UpdatePlaybackProgress(Guid sessionId, int clientReportedSeconds);
}