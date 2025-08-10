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
        _baseUrl = _configuration["Music:External:MusicBrainz:BaseUrl"];
        _baseCoverUrl = _configuration["Music:External:AlbumCover:BaseUrl"];

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            _configuration["Music:External:MusicBrainz:User-Agent"]); 
    }

    public async Task<List<ArtistDto>> SearchArtistsAsync(string query)
    {
        var url = $"{_baseUrl}artist?query={Uri.EscapeDataString(query)}&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);

        return response.Artists.ToList();
    }
    
    public async Task<List<RecordingDto>> SearchRecordingsAsync(string query)
    {
        var url = $"{_baseUrl}recording?query={Uri.EscapeDataString(query)}&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);
        
        return response.Recordings.ToList();
    }
    
    public async Task<List<RecordingDto>> SearchAlbumRecordingsAsync(string query)
    {
        var releases = await GetReleasesFromQuery(query);
        var albumId = releases.First().Id;
        
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
        var recordings = await GetRecordingsFromAlbum(albumId);
        return recordings.Select(recording =>
            {
                recording.Releases = releases;
                return recording;
            })
            .ToList();
    }
    
    private async Task<ICollection<ReleaseDto>> GetReleasesFromId(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var release = await _httpClient.GetFromJsonAsync<ReleaseDto>(releaseUrl);
        release.Artist = release.ArtistCredit.FirstOrDefault().Artist;
        
        if (release == null)
            return Array.Empty<ReleaseDto>();

        release.Cover = await GetAlbumCover(albumId);
        return new List<ReleaseDto> { release };
    }
    
    private async Task<ICollection<ReleaseDto>> GetReleasesFromQuery(string query)
    {
        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(query)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);

        var releaseGroup = searchResponse.ReleaseGroups.First();
        var releases = await Task.WhenAll(
            releaseGroup.Releases
                .Take(5)
                .Select(async release =>
            {
                release.Artist = releaseGroup.ArtistCredits.First().Artist;
                release.Cover = await GetAlbumCover(release.Id);
                return release;
            })
        );

        return releases.ToList();
    }

    public async Task<ReleaseResponse> GetAlbumDetails(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzLookupResponse>(releaseUrl);
        var albumCover = await GetAlbumCover(albumId);

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
        var albumCover = await GetAlbumCover(albumId);

        var positionCounter = 1;
        var recordings = response
            .Media
            .SelectMany(media => media.Tracks)
            .Select(track =>
            {
                track.Recording.Cover = albumCover;
                track.Recording.PositionInAlbum = positionCounter++;
                return track.Recording;
            })
            .ToList();
        
        return recordings;
    }
    
    
    private async Task<string> GetAlbumCover(Guid albumId)
    {
        var coverUrl = $"{_baseCoverUrl}release/{albumId}";
        var response = await _httpClient.GetFromJsonAsync<CoverArtResponse>(coverUrl);

 
        return response.Images.First().Image;
    }
}