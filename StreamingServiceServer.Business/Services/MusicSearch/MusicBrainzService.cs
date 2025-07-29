using System.Net.Http.Json;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;

public class MusicBrainzService : IExternalMusicSearchService
{
    private readonly StreamingDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://musicbrainz.org/ws/2/";

    public MusicBrainzService(StreamingDbContext dbContext, HttpClient httpClient)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "StreamingService/1.0 (tessadt@tesasdast.com)"); 
    }

    public async Task<List<ArtistDto>> SearchArtistsAsync(string query)
    {
        var url = $"{BaseUrl}artist?query={Uri.EscapeDataString(query)}&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(url);

        return response.Artists.ToList();
    }
    
    public async Task<List<RecordingDto>> SearchRecordingsAsync(string query)
    {
        var url = $"{BaseUrl}recording?query={Uri.EscapeDataString(query)}&fmt=json";
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
        var releaseGroupUrl = $"{BaseUrl}release-group?query={Uri.EscapeDataString(query)}&fmt=json";
        var searchResponse = await _httpClient.GetFromJsonAsync<MusicBrainzSearchResponse>(releaseGroupUrl);

        return searchResponse.ReleaseGroups.First().Releases;
    }

    private async Task<ICollection<RecordingDto>> GetRecordingsFromAlbum(Guid albumId)
    {
        var releaseUrl = $"{BaseUrl}release/{albumId}?inc=recordings+artist-credits&fmt=json";
        var response = await _httpClient.GetFromJsonAsync<MusicBrainzLookupResponse>(releaseUrl);

        var recordings = response
            .Media.First()
            .Tracks.Select(track => track.Recording)
            .ToList();
        
        return recordings;
    }
}