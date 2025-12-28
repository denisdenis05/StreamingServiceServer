using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public class CoverOptimizationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoverOptimizationService> _logger;
    private readonly IConfiguration _configuration;

    public CoverOptimizationService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<CoverOptimizationService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await OptimizeReleasesAsync(stoppingToken);
                await OptimizeRecordingsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while optimizing covers.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task OptimizeReleasesAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

        var releases = await dbContext.Releases
            .Include(r => r.Recordings)
            .Where(r => r.OptimizedCover == "" || r.OptimizedSmallCover == "" || r.OptimizedVerySmallCover == "")
            .OrderByDescending(r => r.DateAdded)
            .Take(50) 
            .ToListAsync(stoppingToken);

        foreach (var release in releases)
        {
            if (stoppingToken.IsCancellationRequested) break;

            await OptimizeEntityAsync(release, dbContext, stoppingToken);
            
            if (!string.IsNullOrEmpty(release.OptimizedCover) || 
                !string.IsNullOrEmpty(release.OptimizedSmallCover) || 
                !string.IsNullOrEmpty(release.OptimizedVerySmallCover))
            {
                foreach (var recording in release.Recordings)
                {
                    bool changed = false;
                    if (!string.IsNullOrEmpty(release.OptimizedCover) && string.IsNullOrEmpty(recording.OptimizedCover))
                    {
                        recording.OptimizedCover = release.OptimizedCover;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(release.OptimizedSmallCover) && string.IsNullOrEmpty(recording.OptimizedSmallCover))
                    {
                        recording.OptimizedSmallCover = release.OptimizedSmallCover;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(release.OptimizedVerySmallCover) && string.IsNullOrEmpty(recording.OptimizedVerySmallCover))
                    {
                        recording.OptimizedVerySmallCover = release.OptimizedVerySmallCover;
                        changed = true;
                    }
                    
                    if (changed)
                    {
                        _logger.LogInformation("Propagated optimized covers from Release {ReleaseId} to Recording {RecordingId}", release.Id, recording.Id);
                    }
                }
                await dbContext.SaveChangesAsync(stoppingToken);
            }
        }
    }

    private async Task OptimizeRecordingsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

        var recordings = await dbContext.Recordings
            .Where(r => r.OptimizedCover == "" || r.OptimizedSmallCover == "" || r.OptimizedVerySmallCover == "")
            .OrderByDescending(r => r.DateAdded)
            .Take(50)
            .ToListAsync(stoppingToken);

        foreach (var recording in recordings)
        {
            if (stoppingToken.IsCancellationRequested) break;

            await OptimizeEntityAsync(recording, dbContext, stoppingToken);
        }
    }

    private async Task OptimizeEntityAsync(dynamic entity, StreamingDbContext dbContext, CancellationToken stoppingToken)
    {        
        var tasks = new List<Task<(string Type, string Url)>>();

        if (string.IsNullOrEmpty(entity.OptimizedCover) && !string.IsNullOrEmpty(entity.Cover))
        {
            tasks.Add(ResolveUrlAsync("Cover", entity.Cover, stoppingToken));
        }

        if (string.IsNullOrEmpty(entity.OptimizedSmallCover) && !string.IsNullOrEmpty(entity.SmallCover))
        {
            tasks.Add(ResolveUrlAsync("SmallCover", entity.SmallCover, stoppingToken));
        }

        if (string.IsNullOrEmpty(entity.OptimizedVerySmallCover) && !string.IsNullOrEmpty(entity.VerySmallCover))
        {
            tasks.Add(ResolveUrlAsync("VerySmallCover", entity.VerySmallCover, stoppingToken));
        }

        if (tasks.Count == 0) return;

        var results = await Task.WhenAll(tasks);
        bool changed = false;

        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.Url)) continue;

            if (result.Type == "Cover")
            {
                entity.OptimizedCover = result.Url;
                changed = true;
            }
            else if (result.Type == "SmallCover")
            {
                entity.OptimizedSmallCover = result.Url;
                changed = true;
            }
            else if (result.Type == "VerySmallCover")
            {
                entity.OptimizedVerySmallCover = result.Url;
                changed = true;
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Optimized covers for {Type} {Id}", (string)entity.GetType().Name, (object)entity.Id);
        }
    }

    private async Task<(string Type, string Url)> ResolveUrlAsync(string type, string url, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(url)) return (type, string.Empty);

        try
        {
            var client = _httpClientFactory.CreateClient();
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", _configuration["Music:External:MusicBrainz:User-Agent"]);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);

            if (response.IsSuccessStatusCode)
            {
                var finalUrl = response.RequestMessage?.RequestUri?.ToString();
                return (type, finalUrl ?? url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve URL: {Url}", url);
        }

        return (type, string.Empty);
    }
}
