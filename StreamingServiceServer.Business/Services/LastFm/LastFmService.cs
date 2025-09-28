using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Business.Models.LastFm;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.LastFm;

public class LastFmService : ILastFmService
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _redirect;
    private readonly string _baseUrl = "http://ws.audioscrobbler.com/2.0/";
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly StreamingDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public LastFmService(HttpClient httpClient, IConfiguration configuration, StreamingDbContext dbContext, IServiceScopeFactory scopeFactory)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _apiKey = configuration["LastFm:ApiKey"];
        _apiSecret = configuration["LastFm:ApiSecret"];
        _redirect = configuration["LastFm:Redirect"];
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    public async Task<string> GenerateAuthUrl()
    {
        var encodedCallback = Uri.EscapeDataString(_redirect);
        return $"http://www.last.fm/api/auth/?api_key={_apiKey}&cb={encodedCallback}";
    }

    private async Task<string> GetRequestToken()
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = _apiKey,
            ["format"] = "json",
            ["cb"] = _redirect,
        };

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;

        var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value ?? "")}"));
        var requestUrl = $"{_baseUrl}?{queryString}";

        var response = await _httpClient.GetAsync(requestUrl);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Last.fm API error: {response.StatusCode} - {responseContent}");
        }

        var jsonDoc = JsonDocument.Parse(responseContent);
        if (jsonDoc.RootElement.TryGetProperty("token", out var tokenElement))
        {
            return tokenElement.GetString();
        }

        if (jsonDoc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorCode = errorElement.GetInt32();
            var errorMessage = jsonDoc.RootElement.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : "Unknown error";
            throw new Exception($"Last.fm error {errorCode}: {errorMessage}");
        }

        throw new Exception("Failed to get request token from Last.fm");
    }

    public async Task<(string SessionKey, string Username)> GetSessionKeyAndUsername(string token)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["api_key"] = _apiKey,
            ["token"] = token,
            ["format"] = "json"
        };

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;

        var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var requestUrl = $"{_baseUrl}?{queryString}";

        var response = await _httpClient.GetAsync(requestUrl);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Last.fm API error: {response.StatusCode} - {responseContent}");
        }

        var jsonDoc = JsonDocument.Parse(responseContent);

        if (jsonDoc.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorCode = errorElement.GetInt32();
            var errorMessage = jsonDoc.RootElement.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : "Unknown error";
            throw new Exception($"Last.fm error {errorCode}: {errorMessage}");
        }

        if (jsonDoc.RootElement.TryGetProperty("session", out var session))
        {
            var sessionKey = session.TryGetProperty("key", out var key) ? key.GetString() : null;
            var username = session.TryGetProperty("name", out var name) ? name.GetString() : null;

            if (sessionKey != null && username != null)
            {
                return (sessionKey, username);
            }
        }

        throw new Exception("Failed to get session key and username from Last.fm response");
    }

    public async Task<Guid> StartPlaybackSession(Guid userId, StartPlaybackRequest request)
    {
        try
        {
            var sessionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            var listen = new Listen
            {
                ListenId = Guid.NewGuid(),
                UserId = userId,
                RecordingId = request.TrackId,
                SessionId = sessionId,
                StartTime = startTime,
                TrackDurationSeconds = request.DurationSeconds,
                ValidatedPlayedSeconds = 0,
                ClientReportedSeconds = 0,
                ServerCalculatedSeconds = 0,
                LastProgressUpdate = startTime,
                IsActive = true,
                NowPlayingReported = false,
                Scrobbled = false,
                PauseSeekEvents = "[]", 
                HasAnomalies = false
            };

            _dbContext.Listens.Add(listen);
            await _dbContext.SaveChangesAsync();

            await ReportNowPlaying(listen.ListenId);

            return sessionId;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error starting playback session for user {userId}: {ex.Message}", ex);
        }
    }

    public async Task RecordPlaybackEvent(Guid sessionId, PlaybackEvent playbackEvent)
    {
        try
        {
            var listen = await _dbContext.Listens
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.IsActive);

            if (listen != null)
            {
                var events = string.IsNullOrEmpty(listen.PauseSeekEvents)
                    ? new List<PlaybackEvent>()
                    : JsonSerializer.Deserialize<List<PlaybackEvent>>(listen.PauseSeekEvents) ??
                      new List<PlaybackEvent>();

                events.Add(playbackEvent);
                listen.PauseSeekEvents = JsonSerializer.Serialize(events);

                await _dbContext.SaveChangesAsync();
            
                switch (playbackEvent.EventType.ToLower())
                {
                    case "pause":
                        await ClearNowPlayingForListen(listen.ListenId);
                        break;
                    case "resume":
                        await ReportNowPlaying(listen.ListenId);
                        break;
                    case "seek":
                        await ReportNowPlaying(listen.ListenId);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error recording playback event for session {sessionId}: {ex.Message}", ex);
        }
    }

    private async Task ReportNowPlaying(Guid listenId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            var listen = await db.Listens
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.ListenId == listenId);
            
            var sessionInfo = await GetValidatedSession(listen.UserId);

            if (sessionInfo?.IsValid == true)
            {
                var recording = await db.Recordings
                    .Include(rec => rec.Release)
                    .Include(rec => rec.Release.Artist)
                    .FirstOrDefaultAsync(r => r.Id == listen.RecordingId);

                if (recording != null)
                {
                    var success = await UpdateNowPlaying(
                        sessionInfo.SessionKey,
                        recording.Id,
                        recording.Release.Artist.Name,
                        recording.Title,
                        recording.Release.Title,
                        listen.TrackDurationSeconds 
                    );

                    if (success)
                    {
                        listen.NowPlayingReported = true;
                        await db.SaveChangesAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reporting now playing for listen {listenId}: {ex.Message}");
        }
    }
    
    public async Task<bool> ClearNowPlaying(string sessionKey)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = _apiKey,
            ["sk"] = sessionKey,
            ["artist"] = "",
            ["track"] = "" 
        };

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;
        parameters["format"] = "json";

        try
        {
            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(_baseUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error clearing now playing: {ex.Message}");
            return false;
        }
    }
    
    private async Task ScrobbleListen(Guid listenId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            var listen = await db.Listens
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.ListenId == listenId);

            if (listen.Scrobbled) return;

            var sessionInfo = await GetValidatedSession(listen.UserId);

            if (sessionInfo?.IsValid == true)
            {
                var recording = await db.Recordings
                    .Include(r=> r.Release)
                    .Include(r=> r.Release.Artist)
                    .FirstOrDefaultAsync(r => r.Id == listen.RecordingId);

                if (recording != null)
                {
                    var timestamp = ((DateTimeOffset)listen.StartTime).ToUnixTimeSeconds();

                    var success = await ScrobbleTrack(
                        sessionInfo.SessionKey,
                        recording.Release.Artist.Name,
                        recording.Title,
                        recording.Release.Title,
                        timestamp
                    );

                    if (success)
                    {
                        listen.Scrobbled = true;
                        listen.ScrobbledAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scrobbling listen {listenId}: {ex.Message}");
        }
    }

    public async Task<UserSessionInfo?> GetValidatedSession(Guid userId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            
            var user = await db.Users.FindAsync(userId);

            if (user == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(user.LastFmSessionKey))
            {
                return new UserSessionInfo
                {
                    IsValid = false,
                    RequiresReauth = true
                };
            }

            var isValid = await ValidateSessionKey(user.LastFmSessionKey);

            if (isValid)
            {
                return new UserSessionInfo
                {
                    SessionKey = user.LastFmSessionKey,
                    Username = user.LastFmUsername,
                    IsValid = true,
                    RequiresReauth = false
                };
            }
            else
            {
                user.LastFmSessionKey = null;
                user.LastFmUsername = null;
                user.LastFmConnectedAt = null;

                await db.SaveChangesAsync();

                return new UserSessionInfo
                {
                    IsValid = false,
                    RequiresReauth = true
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error validating session for user {userId}: {ex.Message}");
            return null;
        }
    }

    public async Task ProcessPendingScrobbles()
    {
        try
        {
            var pendingScrobbles = await _dbContext.Listens
                .Include(l => l.Recording)
                .Where(l => !l.Scrobbled &&
                            l.ShouldScrobble &&
                            (!l.IsActive || l.EndTime.HasValue))
                .Take(10)
                .ToListAsync();

            foreach (var listen in pendingScrobbles)
            {
                await ScrobbleListen(listen.ListenId);

                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing pending scrobbles: {ex.Message}");
        }
    }

    
    public async Task UpdatePlaybackProgressDelta(Guid sessionId, int deltaListenedSeconds, int totalListenedSeconds)
    {
        try
        {
            var listen = await _dbContext.Listens
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.IsActive);

            if (listen == null) return;

            var now = DateTime.UtcNow;

            var validatedTotalSeconds = ValidateProgressDelta(listen, totalListenedSeconds, now);
        
            if (deltaListenedSeconds < 0)
            {
                listen.HasAnomalies = true;
                listen.AnomalyNotes += $"Negative delta {deltaListenedSeconds}s; ";
                deltaListenedSeconds = 0;
            }

            var timeSinceLastUpdate = (now - listen.LastProgressUpdate).TotalSeconds;
            if (deltaListenedSeconds > timeSinceLastUpdate + 30) 
            {
                listen.HasAnomalies = true;
                listen.AnomalyNotes += $"Delta {deltaListenedSeconds}s > elapsed time {timeSinceLastUpdate}s; ";
            }

            listen.ValidatedPlayedSeconds = validatedTotalSeconds;
            listen.LastProgressUpdate = now;

            await _dbContext.SaveChangesAsync();

            if (ShouldScrobble(validatedTotalSeconds, listen.TrackDurationSeconds) && !listen.Scrobbled)
            {
                await ScrobbleListen(listen.ListenId);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error updating playback progress for session {sessionId}: {ex.Message}", ex);
        }
    }


    public async Task StopPlaybackSessionDelta(Guid sessionId, int totalListenedSeconds)
    {
        try
        {
            var listen = await _dbContext.Listens
                .Include(l => l.User)
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.IsActive);

            if (listen == null) return;

            var now = DateTime.UtcNow;

            var validatedTotalSeconds = ValidateProgressDelta(listen, totalListenedSeconds, now);

            listen.EndTime = now;
            listen.ValidatedPlayedSeconds = validatedTotalSeconds;
            listen.IsActive = false;

            await _dbContext.SaveChangesAsync();

            await ClearNowPlayingForListen(listen.ListenId);

            if (ShouldScrobble(validatedTotalSeconds, listen.TrackDurationSeconds) && !listen.Scrobbled)
            {
                await ScrobbleListen(listen.ListenId);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error stopping playback session {sessionId}: {ex.Message}", ex);
        }
    }

    private bool ValidateListenTime(int totalListenedSeconds, int trackDurationSeconds, TimeSpan sessionDuration)
    {
        if (totalListenedSeconds < 0) return false;
        if (totalListenedSeconds > trackDurationSeconds + 30) return false; 
        if (totalListenedSeconds > sessionDuration.TotalSeconds + 60) return false; 
    
        return true;
    }

    private int ValidateProgressDelta(Listen listen, int totalListenedSeconds, DateTime now)
    {
        var trackDuration = listen.TrackDurationSeconds > 0
            ? listen.TrackDurationSeconds
            : (listen.Recording?.Length ?? 0);

        var sessionDuration = now - listen.StartTime;

        if (!ValidateListenTime(totalListenedSeconds, trackDuration, sessionDuration))
        {
            listen.HasAnomalies = true;
            listen.AnomalyNotes += $"Invalid total listen time {totalListenedSeconds}s for track {trackDuration}s, session {(int)sessionDuration.TotalSeconds}s; ";
        
            var maxPossibleListenTime = Math.Min(trackDuration, (int)sessionDuration.TotalSeconds);
            return Math.Min(totalListenedSeconds, maxPossibleListenTime);
        }

        var previousTotal = listen.ValidatedPlayedSeconds;
        if (totalListenedSeconds < previousTotal - 5) 
        {
            var hasRecentSeek = HasRecentSeekEvent(listen, now);
            if (!hasRecentSeek)
            {
                listen.HasAnomalies = true;
                listen.AnomalyNotes += $"Total listen time decreased from {previousTotal}s to {totalListenedSeconds}s without seek; ";
            }
        }

        return totalListenedSeconds;
    }
    
    private bool HasRecentSeekEvent(Listen listen, DateTime now)
    {
        try
        {
            if (string.IsNullOrEmpty(listen.PauseSeekEvents))
                return false;

            var events = JsonSerializer.Deserialize<List<PlaybackEvent>>(listen.PauseSeekEvents);
        
            return events?.Any(e => e.EventType.ToLower() == "seek" &&
                                    (now - e.Timestamp).TotalSeconds < 15) == true;
        }
        catch
        {
            return false;
        }
    }
    
    private async Task ClearNowPlayingForListen(Guid listenId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            var listen = await db.Listens
                .FirstOrDefaultAsync(l => l.ListenId == listenId);

            if (listen == null) return;

            var sessionInfo = await GetValidatedSession(listen.UserId);

            if (sessionInfo?.IsValid == true)
            {
                await ClearNowPlaying(sessionInfo.SessionKey);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error clearing now playing for listen {listenId}: {ex.Message}");
        }
    }
    
    private static bool ShouldScrobble(int validatedSeconds, int trackDuration)
    {
        const int fourMinutes = 240;
        var halfDuration = trackDuration / 2.0;
        return validatedSeconds >= fourMinutes || validatedSeconds >= halfDuration;
    }

    private string GenerateSignature(Dictionary<string, string> parameters)
    {
        var sortedParams = parameters
            .Where(p => p.Key != "format")
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}{p.Value}")
            .ToArray();

        var paramString = string.Join("", sortedParams) + _apiSecret;

        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(paramString));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    private async Task<bool> ValidateSessionKey(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
            return false;

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "user.getInfo",
            ["api_key"] = _apiKey,
            ["sk"] = sessionKey,
            ["format"] = "json"
        };

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;

        try
        {
            var queryString = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var requestUrl = $"{_baseUrl}?{queryString}";

            var response = await _httpClient.GetAsync(requestUrl);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var jsonDoc = JsonDocument.Parse(responseContent);

            if (jsonDoc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return false;
            }

            if (jsonDoc.RootElement.TryGetProperty("user", out var userElement))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            return false;
        }
    }
    
    private async Task<bool> ScrobbleTrack(string sessionKey, string artist, string track, string album, long timestamp)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.scrobble",
            ["api_key"] = _apiKey,
            ["sk"] = sessionKey,
            ["artist"] = artist,
            ["track"] = track,
            ["timestamp"] = timestamp.ToString()
        };

        if (!string.IsNullOrEmpty(album))
        {
            parameters["album"] = album;
        }

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;
        parameters["format"] = "json";

        var content = new FormUrlEncodedContent(parameters);
        var response = await _httpClient.PostAsync(_baseUrl, content);

        return response.IsSuccessStatusCode;
    }

    private async Task<bool> UpdateNowPlaying(string sessionKey, Guid recordingId, string artist, string track, string album, int duration)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = _apiKey,
            ["sk"] = sessionKey,
            ["artist"] = artist,
            ["track"] = track,
            ["mbid"] = recordingId.ToString(),
        };

        if (!string.IsNullOrEmpty(album))
        {
            parameters["album"] = album;
        }

        if (duration > 0)
        {
            parameters["duration"] = duration.ToString();
        }

        var signature = GenerateSignature(parameters);
        parameters["api_sig"] = signature;
        parameters["format"] = "json";

        try
        {
            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(_baseUrl, content);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            return false;
        }
    }
}