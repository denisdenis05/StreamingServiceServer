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
    private readonly string _baseUrl;

    public MusicBrainzService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _baseUrl = _configuration["Music:External:MusicBrainz:BaseUrl"];

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

    private async Task<ICollection<ReleaseDto>> GetReleasesFromQuery(string query)
    {
        var releaseGroupUrl = $"{_baseUrl}release-group?query={Uri.EscapeDataString(query)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);

        var releaseGroup = searchResponse.ReleaseGroups.First();
        return releaseGroup.Releases
            .Select(release => 
                { release.Artist = releaseGroup.ArtistCredits.First().Artist; 
                    return release; 
                }
                )
            .ToList();
    }

    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzLookupResponse>(releaseUrl);

        var recordings = response
            .Media.First()
            .Tracks.Select(track => track.Recording)
            .ToList();
        
        return recordings;
    }
}