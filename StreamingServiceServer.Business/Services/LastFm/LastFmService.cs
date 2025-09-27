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

    public async Task<bool> ScrobbleTrack(string sessionKey, string artist, string track, string album, long timestamp)
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

    public async Task<bool> UpdateNowPlaying(string sessionKey, string artist, string track, string album, int duration)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = _apiKey,
            ["sk"] = sessionKey,
            ["artist"] = artist,
            ["track"] = track
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

    public async Task<Guid> StartPlaybackSession(Guid userId, StartPlaybackRequest request)
    {
        try
        {
            var sessionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            var recentListen = await _dbContext.Listens
                .Where(l => l.UserId == userId &&
                            l.RecordingId == request.TrackId &&
                            l.IsActive &&
                            l.StartTime > DateTime.UtcNow.AddMinutes(-5))
                .OrderByDescending(l => l.StartTime)
                .FirstOrDefaultAsync();

            if (recentListen != null)
            {
                recentListen.SessionId = sessionId;
                recentListen.IsActive = true;
                await _dbContext.SaveChangesAsync();

                if (!recentListen.NowPlayingReported)
                {
                    _ = Task.Run(() => ReportNowPlaying(recentListen.ListenId));
                }

                return sessionId;
            }

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

            _ = Task.Run(() => ReportNowPlaying(listen.ListenId));

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

            if (listen.NowPlayingReported) return;

            var sessionInfo = await GetValidatedSession(listen.UserId);

            if (sessionInfo?.IsValid == true)
            {
                var recording = await db.Recordings
                    .FirstOrDefaultAsync(r => r.Id == listen.RecordingId);

                if (recording != null)
                {
                    var success = await UpdateNowPlaying(
                        sessionInfo.SessionKey,
                        recording.ArtistCredit.First().Name,
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
                    .FirstOrDefaultAsync(r => r.Id == listen.RecordingId);

                if (recording != null)
                {
                    var timestamp = ((DateTimeOffset)listen.StartTime).ToUnixTimeSeconds();

                    var success = await ScrobbleTrack(
                        sessionInfo.SessionKey,
                        recording.ArtistCredit.First().Name,
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

    public async Task UpdatePlaybackProgress(Guid sessionId, int clientReportedSeconds)
    {
        try
        {
            var listen = await _dbContext.Listens
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.IsActive);

            if (listen == null) return;

            var now = DateTime.UtcNow;
            var serverCalculatedSeconds = (int)(now - listen.StartTime).TotalSeconds;

            var validatedSeconds = ValidateProgress(listen, clientReportedSeconds, serverCalculatedSeconds, now);

            listen.ClientReportedSeconds = clientReportedSeconds;
            listen.ServerCalculatedSeconds = serverCalculatedSeconds;
            listen.ValidatedPlayedSeconds = validatedSeconds;
            listen.LastProgressUpdate = now;

            await _dbContext.SaveChangesAsync();

            if (listen.ShouldScrobble && !listen.Scrobbled)
            {
                _ = Task.Run(() => ScrobbleListen(listen.ListenId));
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error updating playback progress for session {sessionId}: {ex.Message}", ex);
        }
    }

    private int ValidateProgress(Listen listen, int clientReported, int serverCalculated, DateTime now)
    {
        // Use TrackDurationSeconds instead of Recording.Length for consistency
        var trackDuration = listen.TrackDurationSeconds > 0
            ? listen.TrackDurationSeconds
            : (listen.Recording?.Length ?? 0);

        if (clientReported > trackDuration)
        {
            listen.HasAnomalies = true;
            listen.AnomalyNotes += $"Reported {clientReported}s > duration {trackDuration}s; ";
            return Math.Min(clientReported, trackDuration);
        }

        var previousValidated = listen.ValidatedPlayedSeconds;
        if (clientReported < previousValidated - 10) 
        {
            var recentSeek = HasRecentSeekEvent(listen, now);

            if (!recentSeek)
            {
                listen.HasAnomalies = true;
                listen.AnomalyNotes += $"Backwards progress {previousValidated}s -> {clientReported}s; ";
            }
        }

        var timeSinceStart = (now - listen.StartTime).TotalSeconds;
        var maxPossibleProgress = timeSinceStart + 30; 

        if (clientReported > maxPossibleProgress)
        {
            listen.HasAnomalies = true;
            listen.AnomalyNotes += $"Too rapid progress {clientReported}s > {maxPossibleProgress}s; ";
            return (int)Math.Min(clientReported, maxPossibleProgress);
        }

        var timeSinceLastUpdate = (now - listen.LastProgressUpdate).TotalSeconds;
        if (timeSinceLastUpdate > 120)
        {
            var expectedProgress = previousValidated + timeSinceLastUpdate;
            var actualProgress = clientReported;

            if (Math.Abs(actualProgress - expectedProgress) > 60)
            {
                listen.HasAnomalies = true;
                listen.AnomalyNotes += $"Large reporting gap {timeSinceLastUpdate}s; ";
            }
        }

        var variance = Math.Abs(clientReported - serverCalculated);

        if (variance <= 30)
        {
            return clientReported;
        }
        else if (variance <= 60)
        {
            return (int)((clientReported + serverCalculated) / 2.0);
        }

        return serverCalculated;
    }

    private bool HasRecentSeekEvent(Listen listen, DateTime now)
    {
        try
        {
            if (string.IsNullOrEmpty(listen.PauseSeekEvents))
                return false;

            var events = JsonSerializer.Deserialize<List<PlaybackEvent>>(listen.PauseSeekEvents);
            return events?.Any(e => e.EventType == "seek" &&
                                    (now - e.Timestamp).TotalSeconds < 10) == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopPlaybackSession(Guid sessionId, int clientReportedSeconds)
    {
        try
        {
            var listen = await _dbContext.Listens
                .Include(l => l.User)
                .Include(l => l.Recording)
                .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.IsActive);

            if (listen == null) return;

            var now = DateTime.UtcNow;
            var totalServerTime = (int)(now - listen.StartTime).TotalSeconds;

            var finalValidatedSeconds = ValidateProgress(listen, clientReportedSeconds, totalServerTime, now);

            listen.EndTime = now;
            listen.ClientReportedSeconds = clientReportedSeconds;
            listen.ServerCalculatedSeconds = totalServerTime;
            listen.ValidatedPlayedSeconds = finalValidatedSeconds;
            listen.IsActive = false;

            await _dbContext.SaveChangesAsync();

            if (ShouldScrobble(finalValidatedSeconds, listen.TrackDurationSeconds) && !listen.Scrobbled)
            {
                _ = Task.Run(() => ScrobbleListen(listen.ListenId));
            }
        }
        catch (Exception ex)
        {
            throw;
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
}