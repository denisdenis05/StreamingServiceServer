using System.Net.Http.Json;
using StreamingServiceServer.Business.Services.MusicSearch.Requests;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public partial class MusicBrainzService : IExternalMusicSearchService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl, _baseCoverUrl;

    private readonly IMusicBrainzRequestQueue requestQueue;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<ReleaseDto> Results, DateTime CachedAt)> _albumSearchCache = new();
    private static readonly TimeSpan _albumCacheTtl = TimeSpan.FromMinutes(5);

    public MusicBrainzService(HttpClient httpClient, IConfiguration configuration, IMusicBrainzRequestQueue? requestQueue = null)
    {
        _httpClient = httpClient;
        this.requestQueue = requestQueue ?? MusicBrainzRequestQueue.Shared;
        _configuration = configuration;
        _baseUrl = _configuration["Music:External:MusicBrainz:BaseUrl"] ?? "https://musicbrainz.org/ws/2/";
        _baseCoverUrl = _configuration["Music:External:AlbumCover:BaseUrl"] ?? "https://coverartarchive.org/release/";

        var userAgent = _configuration["Music:External:MusicBrainz:User-Agent"] ?? MusicBrainzRequestQueue.DefaultUserAgent;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
        }
    }

    private async Task<T?> GetJsonAsync<T>(string requestAddress, CancellationToken cancellationToken = default)
    {
        using var response = await requestQueue.SendAsync(
            requestCancellation => _httpClient.GetAsync(requestAddress, requestCancellation), cancellationToken);
        if (!response.IsSuccessStatusCode) return default;
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<List<ArtistDto>> SearchArtistsAsync(string query)
    {
        var url = $"{_baseUrl}artist?query={Uri.EscapeDataString(query)}&fmt=json";
        var response = await GetJsonAsync<MusicBrainzSearchResponse>(url);
        return response?.Artists?.ToList() ?? new List<ArtistDto>();
    }

    public async Task<List<RecordingDto>> SearchRecordingsAsync(string query)
    {
        var luceneQuery = $"\"{query}\" OR (recording:\"{query}\"^1.5 AND artist:\"{query}\")";
        var url = $"{_baseUrl}recording?query={Uri.EscapeDataString(luceneQuery)}&inc=releases+artist-credits&fmt=json";
        var response = await GetJsonAsync<MusicBrainzSearchResponse>(url);

        return response?.Recordings?.ToList() ?? new List<RecordingDto>();
    }

    public async Task<List<ReleaseDto>> SearchReleaseGroupsAsync(string query)
    {
        var luceneQuery = $"\"{query}\" OR (release:\"{query}\"^1.5 AND artist:\"{query}\")";
        var url = $"{_baseUrl}release-group?query={Uri.EscapeDataString(luceneQuery)}&fmt=json";
        var searchResponse = await GetJsonAsync<MusicBrainzSearchResponse>(url);

        var releaseGroups = searchResponse?.ReleaseGroups?.Take(5).ToList() ?? new List<ReleaseGroup>();

        var releases = new List<ReleaseDto>();
        foreach (var group in releaseGroups)
        {
            var releaseToAdd = group.Releases?.FirstOrDefault();
            if (releaseToAdd == null) continue;

            var artist = group.ArtistCredits?.FirstOrDefault()?.Artist;
            if (artist != null)
                releaseToAdd.Artist = artist;

            releases.Add(releaseToAdd);
        }

        return releases;
    }

    public async Task<RecordingDto> SearchRecordingByIdAsync(Guid id)
    {
        var url = $"{_baseUrl}recording/{id}?inc=artist-credits&fmt=json";
        var response = await GetJsonAsync<RecordingDto>(url);
        return response ?? new RecordingDto();
    }

    public async Task<List<ReleaseDto>> SearchAlbumsAsync(string albumQuery, string? artistQuery = null)
    {
        var releases = await SearchForAlbumByQuery(albumQuery, artistQuery);
        return releases.ToList();
    }

    public async Task<List<RecordingDto>> SearchAlbumRecordingsAsync(string query)
    {
        var releases = await GetReleasesFromAlbumByQuery(query);
        var firstRelease = releases.FirstOrDefault();

        if (firstRelease == null)
            return new List<RecordingDto>();

        var albumId = firstRelease.Id;

        var recordings = await GetRecordingsFromAlbum(albumId);
        return recordings.Select(recording =>
            {
                recording.Releases = releases;
                return recording;
            })
            .ToList();
    }

    public async Task<List<RecordingDto>> SearchAlbumRecordingsByIdAsync(Guid albumId)
    {
        var releases = await GetReleasesFromId(albumId);
        if (releases == null || !releases.Any())
            return new List<RecordingDto>();

        var recordings = await GetRecordingsFromAlbum(albumId);
        return recordings.Select(recording =>
            {
                recording.Releases = releases;
                return recording;
            })
            .ToList();
    }

    public async Task<ICollection<ReleaseDto>> GetReleasesFromId(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits+release-groups+media&fmt=json";
        var release = await GetJsonAsync<ReleaseDto>(releaseUrl);
        if (release == null)
            return Array.Empty<ReleaseDto>();

        release.Artist = release.ArtistCredit?.FirstOrDefault()?.Artist;
        return new List<ReleaseDto> { release };
    }

    private async Task<ICollection<ReleaseDto>> GetReleasesFromAlbumByQuery(string query)
    {
        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(query)}&fmt=json";
        var searchResponse = await GetJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);
        if (searchResponse?.ReleaseGroups == null)
            return new List<ReleaseDto>();

        var releaseGroup = searchResponse.ReleaseGroups.FirstOrDefault();
        if (releaseGroup == null)
            return new List<ReleaseDto>();

        var releases = releaseGroup.Releases?.Take(5).ToList() ?? new List<ReleaseDto>();
        var artist = releaseGroup.ArtistCredits?.FirstOrDefault()?.Artist;

        foreach (var release in releases)
        {
            if (artist != null)
                release.Artist = artist;
        }

        return releases;
    }

    private async Task<ICollection<ReleaseDto>> SearchForAlbumByQuery(string query, string? artistQuery = null)
    {
        var cacheKey = $"{query?.ToLowerInvariant()}|{artistQuery?.ToLowerInvariant()}";
        if (_albumSearchCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < _albumCacheTtl)
            return cached.Results;

        var searchQuery = artistQuery != null
            ? $"release:\"{query}\" AND artist:\"{artistQuery}\""
            : query ?? string.Empty;

        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(searchQuery)}&fmt=json";
        var searchResponse = await GetJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);
        if (searchResponse?.ReleaseGroups == null)
            return new List<ReleaseDto>();

        var releases = new List<ReleaseDto>();
        foreach (var releaseToCheck in searchResponse.ReleaseGroups.Take(8))
        {
            var releaseToAdd = releaseToCheck.Releases?.FirstOrDefault();
            if (releaseToAdd == null) continue;

            var artist = releaseToCheck.ArtistCredits?.FirstOrDefault()?.Artist;
            if (artist != null)
                releaseToAdd.Artist = artist;

            releases.Add(releaseToAdd);
        }

        _albumSearchCache[cacheKey] = (releases, DateTime.UtcNow);
        return releases;
    }

    public async Task<ReleaseResponse> GetAlbumDetails(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await GetJsonAsync<MusicBrainzLookupResponse>(releaseUrl);

        var release = new ReleaseResponse
        {
            Id = albumId,
            Title = response?.Title ?? "",
            ArtistName = response?.ArtistCredit?.FirstOrDefault()?.Name ?? "",
            Cover = null
        };

        return release;
    }

    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await GetJsonAsync<MusicBrainzLookupResponse>(releaseUrl);
        if (response?.Media == null)
            return Array.Empty<RecordingDto>();

        var positionCounter = 1;
        var recordings = response
            .Media
            .SelectMany(media => media.Tracks ?? new List<TrackDto>())
            .Select(track =>
            {
                if (track.Recording != null)
                {
                    track.Recording.PositionInAlbum = positionCounter++;
                }
                return track.Recording!;
            })
            .Where(r => r != null)
            .ToList();

        return recordings;
    }

    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(ReleaseDto release)
    {
        if (release?.Media == null)
            return Array.Empty<RecordingDto>();

        var positionCounter = 1;
        var recordings = release
            .Media
            .SelectMany(media => media.Tracks ?? new List<TrackDto>())
            .Select(track =>
            {
                if (track.Recording != null)
                {
                    track.Recording.PositionInAlbum = positionCounter++;
                }
                return track.Recording!;
            })
            .Where(r => r != null)
            .ToList();

        return recordings;
    }
}

