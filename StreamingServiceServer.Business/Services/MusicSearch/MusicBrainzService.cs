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

    public async Task<List<ReleaseDto>> SearchAlbumsAsync(string albumQuery, string? artistQuery = null)
    {
        var releases = await SearchForAlbumByQuery(albumQuery, artistQuery);

        return releases.ToList();
    }

    public async Task<List<RecordingDto>> SearchAlbumRecordingsAsync(string query)
    {
        var releases = await GetReleasesFromAlbumByQuery(query);
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
    
    public async Task<ICollection<ReleaseDto>> GetReleasesFromId(Guid albumId)
    {
        var releaseUrl = $"{_baseUrl}release/{albumId}?inc=recordings+artist-credits+release-groups&fmt=json";
        var release = await _httpClient.GetFromJsonAsync<ReleaseDto>(releaseUrl);
        release.Artist = release.ArtistCredit.FirstOrDefault().Artist;
        
        if (release == null)
            return Array.Empty<ReleaseDto>();

        return new List<ReleaseDto> { release };
    }
    
    private async Task<ICollection<ReleaseDto>> GetReleasesFromAlbumByQuery(string query)
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
                return release;
            })
        );

        return releases.ToList();
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
            var releaseToAdd = releaseToCheck.Releases.FirstOrDefault(); 
            releaseToAdd.Artist = releaseToCheck.ArtistCredits.First().Artist; 
            
            releases.AddRange(releaseToAdd);    
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
    
    
    public async Task<AlbumCoversDto> GetAlbumCover(Guid releaseId, Guid? releaseGroupId = null)
    {
        var albumCovers = new AlbumCoversDto();
        var targetUrlPrefix = releaseGroupId.HasValue
            ? $"{_baseCoverUrl}release-group/{releaseGroupId.Value}"
            : $"{_baseCoverUrl}release/{releaseId}";

        try
        {
            var frontResponse = await _httpClient.GetAsync($"{targetUrlPrefix}/front");
            if (frontResponse.IsSuccessStatusCode)
            {
                albumCovers.Cover = frontResponse.RequestMessage.RequestUri?.AbsoluteUri;
            }

            var front500Response = await _httpClient.GetAsync($"{targetUrlPrefix}/front-500");
            if (front500Response.IsSuccessStatusCode)
            {
                albumCovers.SmallCover = front500Response.RequestMessage.RequestUri?.AbsoluteUri;
            }
            
            var front250Response = await _httpClient.GetAsync($"{targetUrlPrefix}/front-250");
            if (front250Response.IsSuccessStatusCode)
            {
                albumCovers.VerySmallCover = front250Response.RequestMessage.RequestUri?.AbsoluteUri;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Error fetching album covers for release/release group {releaseId}: {ex.Message}");
            return new AlbumCoversDto();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred while fetching album covers for {releaseId}: {ex.Message}");
            return new AlbumCoversDto();
        }

        return albumCovers;
    }
    
    public async Task<AlbumCoversDto> GetAllAlbumCovers(Guid releaseGroupId)
    {
        var albumCovers = new AlbumCoversDto();
        try
        {
            var frontCoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front";
            var frontCoverResponse = await _httpClient.GetAsync(frontCoverUrl);
            if (frontCoverResponse.IsSuccessStatusCode)
            {
                albumCovers.Cover = frontCoverResponse.RequestMessage.RequestUri?.AbsoluteUri;
            }

            var front500CoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front-500";
            var front500CoverResponse = await _httpClient.GetAsync(front500CoverUrl);
            if (front500CoverResponse.IsSuccessStatusCode)
            {
                albumCovers.SmallCover = front500CoverResponse.RequestMessage.RequestUri?.AbsoluteUri;
            }

            var front250CoverUrl = $"{_baseCoverUrl}release-group/{releaseGroupId}/front-250";
            var front250CoverResponse = await _httpClient.GetAsync(front250CoverUrl);
            if (front250CoverResponse.IsSuccessStatusCode)
            {
                albumCovers.VerySmallCover = front250CoverResponse.RequestMessage.RequestUri?.AbsoluteUri;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Error fetching album covers for release group {releaseGroupId}: {ex.Message}");
            return new AlbumCoversDto();
        }
        return albumCovers;
    }
}