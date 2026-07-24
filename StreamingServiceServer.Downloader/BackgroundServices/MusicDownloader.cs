using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Helpers;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

public class MusicDownloader : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public MusicDownloader(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DownloadAlbumsAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[MusicDownloader] Error: {exception.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task DownloadAlbumsAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(3000, cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var configService = scope.ServiceProvider.GetRequiredService<DownloadProviderConfigService>();

        var albumsToDownload = dbContext.ReleasesToDownload
            .Where(release => !dbContext.PendingDownloads.Any(p => p.Id == release.Id))
            .Where(release => !dbContext.FailedDownloads.Any(p => p.Id == release.Id))
            .ToList();

        if (!albumsToDownload.Any())
            return;


        var providers = await configService.GetActiveProvidersOrderedAsync();
        if (providers.Count == 0)
        {
            Console.WriteLine("[MusicDownloader] No enabled download providers configured.");
            return;
        }

        foreach (var album in albumsToDownload)
        {
            bool downloadStarted = false;

            foreach (var (provider, config) in providers)
            {
                try
                {
                    var formatOrder = (config.FormatPreferences ?? "FLAC,WAV,MP3,ANY")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    Console.WriteLine($"[MusicDownloader] Trying {provider.ProviderName} for '{album.Artist} - {album.Title}'...");

                    var result = await provider.SearchAndEnqueueAsync(album, formatOrder, cancellationToken);

                    if (result != null && result.Success)
                    {
                        Console.WriteLine($"[MusicDownloader] {provider.ProviderName} found match: {result.SourceIdentifier}");

                        await dbContext.PendingDownloads.AddAsync(new PendingDownload
                        {
                            Id = album.Id,
                            SourceName = result.SourceIdentifier,
                            ProviderName = provider.ProviderName,
                            SavePath = result.SavePath
                        }, cancellationToken);
                        await dbContext.SaveChangesAsync(cancellationToken);

                        downloadStarted = true;
                        break;
                    }

                    Console.WriteLine($"[MusicDownloader] {provider.ProviderName} did not find a match.");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[MusicDownloader] {provider.ProviderName} failed: {exception.Message}");
                }
            }

            if (!downloadStarted)
            {
                Console.WriteLine($"[MusicDownloader] All providers failed for '{album.Artist} - {album.Title}'. Marking as failed.");

                await dbContext.FailedDownloads.AddAsync(new FailedDownload
                {
                    Id = album.Id,
                    Title = album.Title,
                    Artist = album.Artist,
                }, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
