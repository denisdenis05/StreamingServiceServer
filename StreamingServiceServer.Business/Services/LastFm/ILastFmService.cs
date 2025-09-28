using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.LastFm;

public interface ILastFmService
{
    public Task<string> GenerateAuthUrl();
    Task<(string SessionKey, string Username)> GetSessionKeyAndUsername(string token);
    Task<Guid> StartPlaybackSession(Guid userId, StartPlaybackRequest request);
    Task RecordPlaybackEvent(Guid sessionId, PlaybackEvent playbackEvent);
    Task StopPlaybackSessionDelta(Guid sessionId, int totalListenedSeconds);
    Task UpdatePlaybackProgressDelta(Guid sessionId, int deltaListenedSeconds, int totalListenedSeconds);
}