using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public class DownloadCoverOptimizationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DownloadCoverOptimizationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _coverPath;
    private readonly string _domainExtension;
    private readonly string _domain;

    public DownloadCoverOptimizationService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<DownloadCoverOptimizationService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _coverPath = _configuration["Cover:Path"];
        _domainExtension = _configuration["Cover:DomainExtension"];
        _domain = _configuration["Domain"];
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
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error occurred while downloading optimized covers.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    private async Task OptimizeReleasesAsync(CancellationToken stoppingToken)
    {
        int offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            var releases = await dbContext.Releases
                .Include(r => r.Recordings)
                .OrderBy(r => r.DateAdded)
                .Skip(offset)
                .Take(10)
                .ToListAsync(stoppingToken);

            if (!releases.Any()) break;

            bool batchChanged = false;
            foreach (var release in releases)
            {
                if (stoppingToken.IsCancellationRequested) break;

                bool changed = await OptimizeEntityAsync(release, dbContext, stoppingToken);
                if (changed) batchChanged = true;

                if (!string.IsNullOrEmpty(release.LocalCover) || 
                    !string.IsNullOrEmpty(release.LocalSmallCover) || 
                    !string.IsNullOrEmpty(release.LocalVerySmallCover))
                {
                    foreach (var recording in release.Recordings)
                    {
                        bool recChanged = false;
                        if (!string.IsNullOrEmpty(release.LocalCover) && string.IsNullOrEmpty(recording.LocalCover))
                        {
                            recording.LocalCover = release.LocalCover;
                            recChanged = true;
                        }
                        if (!string.IsNullOrEmpty(release.LocalSmallCover) && string.IsNullOrEmpty(recording.LocalSmallCover))
                        {
                            recording.LocalSmallCover = release.LocalSmallCover;
                            recChanged = true;
                        }
                        if (!string.IsNullOrEmpty(release.LocalVerySmallCover) && string.IsNullOrEmpty(recording.LocalVerySmallCover))
                        {
                            recording.LocalVerySmallCover = release.LocalVerySmallCover;
                            recChanged = true;
                        }

                        if (recChanged)
                        {
                            batchChanged = true;
                            _logger.LogInformation("Propagated local covers from Release {ReleaseId} to Recording {RecordingId}", release.Id, recording.Id);
                        }
                    }
                }
            }

            if (batchChanged)
            {
                await dbContext.SaveChangesAsync(stoppingToken);
            }

            offset += 10;
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task OptimizeRecordingsAsync(CancellationToken stoppingToken)
    {
        int offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();

            var recordings = await dbContext.Recordings
                .OrderBy(r => r.DateAdded)
                .Skip(offset)
                .Take(10)
                .ToListAsync(stoppingToken);

            if (!recordings.Any()) break;

            bool batchChanged = false;
            foreach (var recording in recordings)
            {
                if (stoppingToken.IsCancellationRequested) break;

                bool changed = await OptimizeEntityAsync(recording, dbContext, stoppingToken);
                if (changed) batchChanged = true;
            }

            if (batchChanged)
            {
                await dbContext.SaveChangesAsync(stoppingToken);
            }

            offset += 10;
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task<bool> OptimizeEntityAsync(dynamic entity, StreamingDbContext dbContext, CancellationToken stoppingToken)
    {        
        var tasks = new List<Task<(string Type, string LocalPath)>>();

        if (string.IsNullOrEmpty(entity.LocalCover) && !string.IsNullOrEmpty(entity.OptimizedCover))
        {
            tasks.Add(DownloadAndSaveAsync("LocalCover", entity.OptimizedCover, stoppingToken));
        }

        if (string.IsNullOrEmpty(entity.LocalSmallCover) && !string.IsNullOrEmpty(entity.OptimizedSmallCover))
        {
            tasks.Add(DownloadAndSaveAsync("LocalSmallCover", entity.OptimizedSmallCover, stoppingToken));
        }

        if (string.IsNullOrEmpty(entity.LocalVerySmallCover) && !string.IsNullOrEmpty(entity.OptimizedVerySmallCover))
        {
            tasks.Add(DownloadAndSaveAsync("LocalVerySmallCover", entity.OptimizedVerySmallCover, stoppingToken));
        }

        if (tasks.Count == 0) return false;

        var results = await Task.WhenAll(tasks);
        bool changed = false;

        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.LocalPath)) continue;

            if (result.Type == "LocalCover")
            {
                entity.LocalCover = result.LocalPath;
                changed = true;
            }
            else if (result.Type == "LocalSmallCover")
            {
                entity.LocalSmallCover = result.LocalPath;
                changed = true;
            }
            else if (result.Type == "LocalVerySmallCover")
            {
                entity.LocalVerySmallCover = result.LocalPath;
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogInformation("Downloaded optimized covers for {Type} {Id}", (string)entity.GetType().Name, (object)entity.Id);
        }
        return changed;
    }

    private async Task<(string Type, string LocalPath)> DownloadAndSaveAsync(string type, string url, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(url)) return (type, string.Empty);

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, stoppingToken);

            if (response.IsSuccessStatusCode)
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                var localFilePath = Path.Combine(_coverPath, fileName);

                Directory.CreateDirectory(_coverPath);

                if (!File.Exists(localFilePath))
                {
                    var imageBytes = await response.Content.ReadAsByteArrayAsync(stoppingToken);
                    await File.WriteAllBytesAsync(localFilePath, imageBytes, stoppingToken);
                }

                var publicUrl = $"{_domain}{_domainExtension}{fileName}";
                return (type, publicUrl);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to download cover from URL: {Url}", url);
        }

        return (type, string.Empty);
    }
}
