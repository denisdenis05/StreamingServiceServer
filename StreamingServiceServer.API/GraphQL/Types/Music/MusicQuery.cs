using HotChocolate;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.API.GraphQL.Types.Music;


public class MusicQuery
{
    private readonly IMetadataService _metadataService;

    public MusicQuery(IMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<Artist>> GetArtists(
        [Service] StreamingDbContext context, 
        string? searchQuery = null)
    {
        var query = context.Artists.AsQueryable();
        
        if (!string.IsNullOrEmpty(searchQuery))
        {
            query = query.Where(a => a.Name.Contains(searchQuery));
        }
        
        return query;
    }

    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Recording> GetRecordings(
        [Service] StreamingDbContext context,
        string? searchQuery = null)
    {
        var query = context.Recordings.AsQueryable();
        
        if (!string.IsNullOrEmpty(searchQuery))
        {
            query = query.Where(r => r.Title.Contains(searchQuery));
        }
        
        return query;
    }

    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Release> GetReleases(
        [Service] StreamingDbContext context,
        string? searchQuery = null)
    {
        var query = context.Releases.AsQueryable();
        
        if (!string.IsNullOrEmpty(searchQuery))
        {
            query = query.Where(r => r.Title != null && r.Title.Contains(searchQuery));
        }
        
        return query;
    }

    public async Task<List<Artist>> SearchExternalArtists(string query)
        => await _metadataService.SearchArtistsAsync(query);
    
}