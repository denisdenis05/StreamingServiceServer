using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;

public class MusicBrainzService : IExternalMusicSearchService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl, _baseCoverUrl;

    public MusicBrainzService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _baseUrl = _configuration["Music:External:MusicBrainz:BaseUrl"] ?? "https://musicbrainz.org/ws/2/";
        _baseCoverUrl = _configuration["Music:External:AlbumCover:BaseUrl"] ?? "https://coverartarchive.org/release/";

        var userAgent = _configuration["Music:External:MusicBrainz:User-Agent"] ?? "StreamingService/1.0.0 (contact@example.com)";
        _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent); 
    }

    public async Task<List<ArtistDto>> SearchArtistsAsync(string query)
    {
        var url = $"{_baseUrl}artist?query={Uri.EscapeDataString(query)}&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);

        return response.Artists.ToList();
    }
    
    public async Task<List<RecordingDto>> SearchRecordingsAsync(string query)
    {
        var luceneQuery = $"\"{query}\" OR (recording:\"{query}\"^1.5 AND artist:\"{query}\")";
        var url = $"{_baseUrl}recording?query={Uri.EscapeDataString(luceneQuery)}&inc=releases+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);
        
        return response.Recordings.ToList();
    }

    public async Task<List<ReleaseDto>> SearchReleaseGroupsAsync(string query)
    {
        var luceneQuery = $"\"{query}\" OR (release:\"{query}\"^1.5 AND artist:\"{query}\")";
        var url = $"{_baseUrl}release-group?query={Uri.EscapeDataString(luceneQuery)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);

        var releaseGroups = searchResponse.ReleaseGroups.Take(5).ToList();
        
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
        var response = await _httpClient.GetFromJsonAsync<RecordingDto>(url);
        
        return response;
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
        var recordings = await GetRecordingsFromAlbum(releases.First());
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
        var release = await _httpClient.GetFromJsonAsync<ReleaseDto>(releaseUrl);
        release.Artist = release.ArtistCredit?.FirstOrDefault()?.Artist;
        
        if (release == null)
            return Array.Empty<ReleaseDto>();

        return new List<ReleaseDto> { release };
    }
    
    private async Task<ICollection<ReleaseDto>> GetReleasesFromAlbumByQuery(string query)
    {
        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(query)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);

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
        var searchQuery = string.Empty;
        if (artistQuery != null)
            searchQuery = $"release:\"{query}\" AND artist:\"{artistQuery}\"";
        else
            searchQuery = query;
        
        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(searchQuery)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);

        var releaseGroup = searchResponse.ReleaseGroups.Take(5).ToList();
        
        var releases = new List<ReleaseDto>();
        foreach (var releaseToCheck in releaseGroup)
        {
            var releaseToAdd = releaseToCheck.Releases?.FirstOrDefault();

            var artist = releaseToCheck.ArtistCredits?.FirstOrDefault()?.Artist;
            if (artist != null)
                releaseToAdd.Artist = artist;
            
            releases.Add(releaseToAdd);    
        }
        
        return releases.ToList();
    }

    public async Task<ReleaseResponse> GetAlbumDetails(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzLookupResponse>(releaseUrl);
        var albumCover = string.Empty;

        var release = new ReleaseResponse
        {
            Id = albumId,
            Title = response.Title,
            ArtistName = response.ArtistCredit.First().Name,
            Cover = null
        };
        
        return release;
    }
    
    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzLookupResponse>(releaseUrl);

        var positionCounter = 1;
        var recordings = response
            .Media
            .SelectMany(media => media.Tracks)
            .Select(track =>
            {
                track.Recording.PositionInAlbum = positionCounter++;
                return track.Recording;
            })
            .ToList();
        
        return recordings;
    }
    
    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(ReleaseDto release)
    {
        var positionCounter = 1;
        var recordings = release
            .Media
            .SelectMany(media => media.Tracks)
            .Select(track =>
            {
                track.Recording.PositionInAlbum = positionCounter++;
                return track.Recording;
            })
            .ToList();
        
        return recordings;
    }
    
    
}