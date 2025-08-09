using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Services.ExternalMusicDownloader;
using StreamingServiceServer.Data;

public class MusicDownloader : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly IExternalMusicDownloader _externalMusicDownloader;
    private readonly StreamingDbContext _dbContext;

    public MusicDownloader(StreamingDbContext dbContext ,IConfiguration config, IExternalMusicDownloader externalMusicDownloader)
    {
        _config = config;
        _dbContext = dbContext;
        _externalMusicDownloader = externalMusicDownloader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DownloadTrackAsync();
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task DownloadTrackAsync()
    {
        var albumsToDownload = _dbContext.ReleasesToDownload.ToList();

        foreach (var album in albumsToDownload)
        {
            var music = await _externalMusicDownloader.ScrapeMusicAsync(album);
        }
    }
}