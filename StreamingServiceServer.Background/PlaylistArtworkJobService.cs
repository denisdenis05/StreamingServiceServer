using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreamingServiceServer.Business.Services.Images.Playlists;

namespace StreamingServiceServer.Background;

public sealed class PlaylistArtworkJobService(PlaylistArtworkQueue queue, IServiceScopeFactory scopes,
    TimeProvider clock, ILogger<PlaylistArtworkJobService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out var request) || request is null)
            {
                await Task.Delay(IdleInterval, clock, stoppingToken);
                continue;
            }
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IPlaylistArtworkService>().RefreshAsync(request.UserId, request.PlaylistId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Playlist artwork could not be refreshed."); }
            finally { queue.Complete(request); }
        }
    }
}
