using System.Net;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;

namespace StreamingServiceDownloader.BackgroundServices;

public class PendingDownloadChecker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITorrentHelper _torrentHelper;
    private readonly IConfiguration _configuration;
    private string _musicLocation;

    public PendingDownloadChecker(
        IServiceScopeFactory scopeFactory, 
        ITorrentHelper torrentHelper, 
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _torrentHelper = torrentHelper;
        _configuration = configuration;
        
        _musicLocation = _configuration["Music:Path"] ?? "./Music";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingDownloadsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PendingDownloadChecker] Error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task CheckPendingDownloadsAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(3000, cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var configService = scope.ServiceProvider.GetRequiredService<DownloadProviderConfigService>();
        var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
        var externalMusicSearchService = scope.ServiceProvider.GetRequiredService<IExternalMusicSearchService>();
        var imageService = scope.ServiceProvider.GetRequiredService<IImageService>();

        var pending = dbContext.PendingDownloads.ToList();

        // Get providers so we can route by ProviderName
        var providers = await configService.GetActiveProvidersOrderedAsync();

        foreach (var item in pending)
        {
            try
            {
                var providerName = item.ProviderName ?? "Torrent";
                var providerEntry = providers.FirstOrDefault(p => 
                    p.provider.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));

                if (providerEntry.provider == null)
                {
                    // Fallback to torrent check for backward compatibility
                    await CheckTorrentPendingAsync(item, scope.ServiceProvider);
                    continue;
                }

                var progressInfo = await providerEntry.provider.CheckProgressAsync(
                    item.SourceName, cancellationToken);

                if (progressInfo != null && progressInfo.Progress >= 1.0)
                {
                    Console.WriteLine($"[PendingDownloadChecker] {providerName} download complete for {item.Id}");

                    var rawSavePath = !string.IsNullOrWhiteSpace(progressInfo.SavePath)
                        ? progressInfo.SavePath
                        : item.SavePath ?? "";

                    // For torrent: use the torrent info save path
                    if (providerName == "Torrent")
                    {
                        var torrentInfo = await _torrentHelper.GetTorrentInfoAsync(item.SourceName);
                        if (torrentInfo != null && !string.IsNullOrWhiteSpace(torrentInfo.SavePath))
                            rawSavePath = torrentInfo.SavePath;
                    }

                    var resolvedSavePath = ResolveSavePath(rawSavePath);
                    if (!string.IsNullOrWhiteSpace(resolvedSavePath))
                    {
                        var files = GetMusicFilesInSavePath(resolvedSavePath);
                        if (files.Count == 0)
                        {
                            Console.WriteLine($"[PendingDownloadChecker] WARNING: No music files found at resolved path '{resolvedSavePath}' (raw: '{rawSavePath}') for album {item.Id}. Will retry...");
                            continue;
                        }

                        var matches = await MatchSongsWithMetadataAsync(item.Id, files, externalMusicSearchService, imageService);
                        var movedCount = MoveMatchedFiles(matches);

                        if (movedCount > 0)
                        {
                            // Save MusicBrainz metadata
                            await metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                            await RemoveAlbumFromDatabaseQueues(item.Id, dbContext);
                            await providerEntry.provider.CleanUpAsync(item.SourceName, true);
                        }
                        else
                        {
                            Console.WriteLine($"[PendingDownloadChecker] WARNING: 0 files matched/moved for album {item.Id} at path '{resolvedSavePath}'. Will retry...");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PendingDownloadChecker] Error processing {item.Id}: {ex.Message}");
            }
        }
    }

    private string ResolveSavePath(string rawSavePath)
    {
        if (string.IsNullOrWhiteSpace(rawSavePath))
            return string.Empty;

        // 1. Direct match if path exists
        if (Directory.Exists(rawSavePath))
            return rawSavePath;

        var downloadBase = _configuration["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";
        const string slskdDefaultDir = "/nas/slskd-data/downloads";

        // 2. Try combining configured DownloadPath with rawSavePath
        var combinedConfigured = Path.Combine(downloadBase, rawSavePath.TrimStart('/', '\\'));
        if (Directory.Exists(combinedConfigured))
            return combinedConfigured;

        // 3. Try combining default slskd dir with rawSavePath
        var combinedSlskd = Path.Combine(slskdDefaultDir, rawSavePath.TrimStart('/', '\\'));
        if (Directory.Exists(combinedSlskd))
            return combinedSlskd;

        // 4. Handle Docker container internal paths (e.g. /app/downloads/username/folder or /downloads/folder)
        var normalized = rawSavePath.Replace('\\', '/');
        if (normalized.Contains("/downloads/"))
        {
            var relativePart = normalized.Substring(normalized.IndexOf("/downloads/") + "/downloads/".Length);
            var alt1 = Path.Combine(downloadBase, relativePart);
            if (Directory.Exists(alt1)) return alt1;

            var alt2 = Path.Combine(slskdDefaultDir, relativePart);
            if (Directory.Exists(alt2)) return alt2;
        }

        // 5. Try using just the leaf folder name in download directories
        var folderName = Path.GetFileName(rawSavePath.TrimEnd('/', '\\'));
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            var alt3 = Path.Combine(downloadBase, folderName);
            if (Directory.Exists(alt3)) return alt3;

            var alt4 = Path.Combine(slskdDefaultDir, folderName);
            if (Directory.Exists(alt4)) return alt4;
        }

        return rawSavePath;
    }

    /// <summary>Backward-compatible torrent check for items without ProviderName.</summary>
    private async Task CheckTorrentPendingAsync(StreamingServiceServer.Data.Models.PendingDownload item, IServiceProvider serviceProvider)
    {
        var dbContext = serviceProvider.GetRequiredService<StreamingDbContext>();
        var metadataService = serviceProvider.GetRequiredService<IMetadataService>();
        var externalMusicSearchService = serviceProvider.GetRequiredService<IExternalMusicSearchService>();
        var imageService = serviceProvider.GetRequiredService<IImageService>();

        var info = await _torrentHelper.GetTorrentInfoAsync(item.SourceName);
        if (info != null && info.Progress >= 1.0)
        {
            var resolvedSavePath = ResolveSavePath(info.SavePath);
            var files = GetMusicFilesInSavePath(resolvedSavePath);
            if (files.Count == 0)
            {
                Console.WriteLine($"[PendingDownloadChecker] WARNING: Torrent complete but 0 files found at '{resolvedSavePath}'. Will retry...");
                return;
            }

            var matches = await MatchSongsWithMetadataAsync(item.Id, files, externalMusicSearchService, imageService);
            var movedCount = MoveMatchedFiles(matches);

            if (movedCount > 0)
            {
                await CleanUpTorrentAndFilesAsync(item.SourceName);
                await metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                await RemoveAlbumFromDatabaseQueues(item.Id, dbContext);
            }
        }
    }
    
    private List<(string fileName, string fullPath)> GetMusicFilesInSavePath(string savePath)
    {
        var result = new List<(string fileName, string fullPath)>();

        if (!Directory.Exists(savePath))
        {
            return result;
        }

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma"
        };

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(savePath, "*.*", SearchOption.AllDirectories))
            {
                if (allowedExtensions.Contains(Path.GetExtension(filePath)))
                {
                    result.Add((Path.GetFileName(filePath), filePath));
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied to some folders: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while searching files: {ex.Message}");
        }

        return result;
    }

    private Task<ICollection<MusicFileMatch>> MatchSongsWithMetadataAsync(
        Guid albumId,
        List<(string fileName, string fullPath)> files,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService)
    {
        return SoulseekMatchingHelper.MatchSongsWithMetadataAsync(albumId, files, externalMusicSearchService, imageService);
    }

    private int MoveMatchedFiles(ICollection<MusicFileMatch> matches)
    {
        var baseMusicPath = _musicLocation;
        int movedCount = 0;

        foreach (var match in matches)
        {
            if (match.Score == 0 || string.IsNullOrWhiteSpace(match.FullPath))
                continue;

            var recording = match.Recording;
            var relativePath = MusicLocationHelper.GetRelativeFolderPathForRecording(recording);

            var destinationDir = Path.Combine(baseMusicPath, relativePath);
            Directory.CreateDirectory(destinationDir);

            var extension = Path.GetExtension(match.FullPath);
            var newFileName = recording.Id + extension;
            var destinationPath = Path.Combine(destinationDir, newFileName);

            try
            {
                File.Move(match.FullPath, destinationPath, overwrite: true);
                movedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to move {match.FileName}: {ex.Message}");
            }
        }

        return movedCount;
    }

    private async Task CleanUpTorrentAndFilesAsync(string torrentName)
    {
        var shouldTorrentClientCleanFiles = true;
        await _torrentHelper.RemoveTorrentAsync(torrentName, shouldTorrentClientCleanFiles);
    }

    private async Task RemoveAlbumFromDatabaseQueues(Guid albumId, StreamingDbContext dbContext)
    {
        var releaseToDownload = await dbContext.ReleasesToDownload
            .FirstOrDefaultAsync(r => r.Id == albumId);
        if (releaseToDownload != null)
        {
            dbContext.ReleasesToDownload.Remove(releaseToDownload);
        }

        var pendingDownload = await dbContext.PendingDownloads
            .FirstOrDefaultAsync(p => p.Id == albumId);
        if (pendingDownload != null)
        {
            dbContext.PendingDownloads.Remove(pendingDownload);
        }

        await dbContext.SaveChangesAsync();
    }

    private AudioMetadata? GetAudioMetadata(string filePath)
    {
        return AudioMetadataHelper.GetAudioMetadata(filePath);
    }
}