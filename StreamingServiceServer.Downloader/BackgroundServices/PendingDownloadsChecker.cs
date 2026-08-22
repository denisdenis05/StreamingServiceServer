using System.Collections.Concurrent;
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

    private sealed class StallTracker
    {
        public double LastSeenProgress { get; set; } = -1;
        public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;
        public bool HasSeenActiveTransfers { get; set; }
        public int RetryCount { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, StallTracker> _stallTrackers = new();
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(30);
    private const int MaxStallRetries = 3;

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
            catch (Exception exception)
            {
                Console.WriteLine($"[PendingDownloadChecker] Error: {exception.Message}");
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
                    await CheckTorrentPendingAsync(item, scope.ServiceProvider);
                    continue;
                }

                var progressInfo = await providerEntry.provider.CheckProgressAsync(
                    item.SourceName, cancellationToken);

                if (progressInfo == null) continue;  // transient API error — skip this cycle

                var tracker = _stallTrackers.GetOrAdd(item.Id, _ => new StallTracker());

                if (progressInfo.State == "Failed")
                {
                    Console.WriteLine($"[PendingDownloadChecker] All transfers failed/cancelled for {item.Id}. Requeueing (attempt {tracker.RetryCount + 1}/{MaxStallRetries})...");
                    await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext);
                    continue;
                }

                if (progressInfo.State == "NoTransfersFound")
                {
                    if (tracker.HasSeenActiveTransfers)
                    {
                        // Transfers were active and are now gone — slskd likely auto-cleaned after completion.
                        // Attempt disk recovery using the last known save path.
                        await TryDiskRecoveryAsync(item, tracker, providerEntry.provider, dbContext,
                            metadataService, externalMusicSearchService, imageService);
                    }
                    else if (DateTime.UtcNow - tracker.LastProgressAt > TimeSpan.FromMinutes(5))
                    {
                        Console.WriteLine($"[PendingDownloadChecker] No transfers ever seen for {item.Id} after 5min. Requeueing...");
                        await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext);
                    }
                    continue;
                }

                if (progressInfo.Progress >= 1.0)
                {
                    tracker.HasSeenActiveTransfers = true;
                    Console.WriteLine($"[PendingDownloadChecker] {providerName} download complete for {item.Id}");

                    var rawSavePath = !string.IsNullOrWhiteSpace(progressInfo.SavePath)
                        ? progressInfo.SavePath
                        : item.SavePath ?? "";

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
                            Console.WriteLine($"[PendingDownloadChecker] WARNING: No music files at '{resolvedSavePath}' (raw: '{rawSavePath}') for {item.Id}. Will retry...");
                            continue;
                        }

                        var matches = await MatchSongsWithMetadataAsync(item.Id, files, externalMusicSearchService, imageService);
                        var movedCount = MoveMatchedFiles(matches);

                        if (movedCount > 0)
                        {
                            await metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                            await RemoveAlbumFromDatabaseQueues(item.Id, dbContext);
                            await providerEntry.provider.CleanUpAsync(item.SourceName, true);
                            _stallTrackers.TryRemove(item.Id, out _);
                        }
                        else
                        {
                            Console.WriteLine($"[PendingDownloadChecker] WARNING: 0 files moved for {item.Id} at '{resolvedSavePath}'. Will retry...");
                        }
                    }
                }
                else
                {
                    // Actively downloading — track progress to detect stalls
                    tracker.HasSeenActiveTransfers = true;

                    if (progressInfo.Progress > tracker.LastSeenProgress + 0.001)
                    {
                        tracker.LastSeenProgress = progressInfo.Progress;
                        tracker.LastProgressAt = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - tracker.LastProgressAt > StallTimeout)
                    {
                        Console.WriteLine($"[PendingDownloadChecker] No progress for {(int)StallTimeout.TotalMinutes}min on {item.Id}. Requeueing (attempt {tracker.RetryCount + 1}/{MaxStallRetries})...");
                        await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext);
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[PendingDownloadChecker] Error processing {item.Id}: {exception.Message}");
            }
        }
    }

    private string ResolveSavePath(string rawSavePath)
    {
        if (string.IsNullOrWhiteSpace(rawSavePath))
            return string.Empty;

        var normalizedPath = rawSavePath.Replace('\\', '/');

        if (Directory.Exists(normalizedPath))
            return normalizedPath;

        if (Directory.Exists(rawSavePath))
            return rawSavePath;

        var downloadBase = _configuration["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";
        const string slskdDefaultDir = "/nas/slskd-data/downloads";

        var cleanRelPath = normalizedPath.TrimStart('/');

        var combinedConfigured = Path.Combine(downloadBase, cleanRelPath);
        if (Directory.Exists(combinedConfigured))
            return combinedConfigured;

        var combinedSlskd = Path.Combine(slskdDefaultDir, cleanRelPath);
        if (Directory.Exists(combinedSlskd))
            return combinedSlskd;

        if (normalizedPath.Contains("/downloads/"))
        {
            var relativePart = normalizedPath.Substring(normalizedPath.IndexOf("/downloads/") + "/downloads/".Length);
            var alt1 = Path.Combine(downloadBase, relativePart);
            if (Directory.Exists(alt1)) return alt1;

            var alt2 = Path.Combine(slskdDefaultDir, relativePart);
            if (Directory.Exists(alt2)) return alt2;
        }

        var folderName = Path.GetFileName(normalizedPath.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(folderName))
        {
            var alt3 = Path.Combine(downloadBase, folderName);
            if (Directory.Exists(alt3)) return alt3;

            var alt4 = Path.Combine(slskdDefaultDir, folderName);
            if (Directory.Exists(alt4)) return alt4;
        }

        return rawSavePath;
    }


    private async Task HandleStalledDownloadAsync(
        StreamingServiceServer.Data.Models.PendingDownload item,
        StallTracker tracker,
        IDownloadProvider provider,
        StreamingDbContext dbContext)
    {
        tracker.RetryCount++;

        try { await provider.CleanUpAsync(item.SourceName, false); } catch { }

        if (tracker.RetryCount > MaxStallRetries)
        {
            Console.WriteLine($"[PendingDownloadChecker] Stall retry limit ({MaxStallRetries}) reached for {item.Id}. Marking as failed.");
            _stallTrackers.TryRemove(item.Id, out _);

            if (!await dbContext.FailedDownloads.AnyAsync(f => f.Id == item.Id))
            {
                var release = await dbContext.ReleasesToDownload.FindAsync(item.Id);
                await dbContext.FailedDownloads.AddAsync(new StreamingServiceServer.Data.Models.FailedDownload
                {
                    Id = item.Id,
                    Title = release?.Title ?? "Unknown",
                    Artist = release?.Artist ?? "Unknown"
                });
            }
        }
        else
        {
            Console.WriteLine($"[PendingDownloadChecker] Stall retry {tracker.RetryCount}/{MaxStallRetries} for {item.Id}. Requeueing for new search...");
            tracker.LastSeenProgress = -1;
            tracker.LastProgressAt = DateTime.UtcNow;
            tracker.HasSeenActiveTransfers = false;
        }

        dbContext.PendingDownloads.Remove(item);
        await dbContext.SaveChangesAsync();
    }

    private async Task TryDiskRecoveryAsync(
        StreamingServiceServer.Data.Models.PendingDownload item,
        StallTracker tracker,
        IDownloadProvider provider,
        StreamingDbContext dbContext,
        IMetadataService metadataService,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService)
    {
        var resolvedPath = ResolveSavePath(item.SavePath ?? "");
        if (string.IsNullOrWhiteSpace(resolvedPath)) return;

        var files = GetMusicFilesInSavePath(resolvedPath);
        if (files.Count == 0) return;

        Console.WriteLine($"[PendingDownloadChecker] Disk recovery: {files.Count} file(s) found for {item.Id} at '{resolvedPath}'.");
        var matches = await MatchSongsWithMetadataAsync(item.Id, files, externalMusicSearchService, imageService);
        var movedCount = MoveMatchedFiles(matches);

        if (movedCount > 0)
        {
            await metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
            await RemoveAlbumFromDatabaseQueues(item.Id, dbContext);
            await provider.CleanUpAsync(item.SourceName, true);
            _stallTrackers.TryRemove(item.Id, out _);
        }
    }

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
        catch (UnauthorizedAccessException exception)
        {
            Console.WriteLine($"Access denied to some folders: {exception.Message}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Error while searching files: {exception.Message}");
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
            catch (Exception)
            {
                try
                {
                    File.Copy(match.FullPath, destinationPath, overwrite: true);
                    try { File.Delete(match.FullPath); } catch { }
                    movedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to move {match.FileName}: {ex.Message}");
                }
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
