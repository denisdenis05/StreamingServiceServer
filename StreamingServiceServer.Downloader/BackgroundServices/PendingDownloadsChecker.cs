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
using StreamingServiceDownloader.Importing;

namespace StreamingServiceDownloader.BackgroundServices;

public class PendingDownloadChecker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITorrentHelper _torrentHelper;
    private readonly IConfiguration _configuration;
    private string _musicLocation;

    private sealed class StallTracker
    {
        public Guid ImportIdentifier { get; init; }
        public double LastSeenProgress { get; set; } = -1;
        public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;
        public bool HasSeenActiveTransfers { get; set; }
        public int RetryCount { get; set; }
        public SoulseekFileRetrySchedule FileRetries { get; } = new();
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

        var pending = await dbContext.PendingDownloads.ToListAsync(cancellationToken);
        var pendingIdentifiers = pending.Select(item => item.Id).ToHashSet();
        foreach (var identifier in _stallTrackers.Keys)
            if (!pendingIdentifiers.Contains(identifier)) _stallTrackers.TryRemove(identifier, out _);


        var providers = await configService.GetActiveProvidersOrderedAsync();

        foreach (var item in pending)
        {
            try
            {
                var providerName = item.ProviderName ?? "Torrent";
                var providerEntry = providers.FirstOrDefault(p =>
                    p.provider.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));

                if (providerEntry.provider is null && providerName != "Torrent")
                {
                    Console.WriteLine($"[PendingDownloadChecker] Provider {providerName} is unavailable for {item.Id}; source is retained.");
                    continue;
                }
                var journal = CreateImportJournal(item);
                if (await journal.ReadAsync(cancellationToken) is not null)
                {
                    var resumed = await ImportAlbumAsync(item, item.SavePath ?? string.Empty, dbContext, metadataService,
                        externalMusicSearchService, imageService,
                        providerEntry.provider is null ? () => CleanUpTorrentAndFilesAsync(item.SourceName)
                            : () => CleanUpCompletedDownloadAsync(providerEntry.provider, item), cancellationToken);
                    if (resumed.Stalled && providerEntry.provider is not null)
                    {
                        var recovery = _stallTrackers.GetOrAdd(item.Id, _ => new StallTracker { ImportIdentifier = item.ImportIdentifier });
                        Console.WriteLine($"[PendingDownloadChecker] Incomplete import for {item.Id} needs a replacement source ({resumed.PublishedTracks}/{resumed.ExpectedTracks} verified tracks).");
                        await HandleStalledDownloadAsync(item, recovery, providerEntry.provider, dbContext, cancellationToken);
                    }
                    continue;
                }
                if (providerEntry.provider == null)
                {
                    await CheckTorrentPendingAsync(item, scope.ServiceProvider, cancellationToken);
                    continue;
                }

                var progressInfo = await providerEntry.provider.CheckProgressAsync(
                    GetProviderSource(item), cancellationToken);

                if (progressInfo == null) continue;  // transient API error — skip this cycle

                var tracker = _stallTrackers.AddOrUpdate(item.Id,
                    _ => new StallTracker { ImportIdentifier = item.ImportIdentifier },
                    (_, existing) => existing.ImportIdentifier == item.ImportIdentifier ? existing
                        : new StallTracker { ImportIdentifier = item.ImportIdentifier, RetryCount = existing.RetryCount });

                if (providerEntry.provider is SoulseekProvider && progressInfo.Progress < 1.0)
                {
                    await using var ownership = await journal.AcquireAsync(cancellationToken);
                    if (!await dbContext.PendingDownloads.AnyAsync(current => current.Id == item.Id
                        && current.ImportIdentifier == item.ImportIdentifier && current.SourceName == item.SourceName,
                        cancellationToken)) continue;
                    var source = SlskdSourceIdentity.Parse(item.SourceName, item.SavePath);
                    if (source is not null && await scope.ServiceProvider.GetRequiredService<SlskdHelper>()
                        .RetryMissingFileAsync(source, tracker.FileRetries, cancellationToken))
                    {
                        tracker.LastProgressAt = DateTime.UtcNow;
                        continue;
                    }
                }

                if (progressInfo.State == "Failed")
                {
                    Console.WriteLine($"[PendingDownloadChecker] All transfers failed/cancelled for {item.Id}. Requeueing (attempt {tracker.RetryCount + 1}/{MaxStallRetries})...");
                    await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext, cancellationToken);
                    continue;
                }

                if (progressInfo.State == "NoTransfersFound")
                {
                    if (tracker.HasSeenActiveTransfers)
                    {
                        // Transfers were active and are now gone — slskd likely auto-cleaned after completion.
                        // Attempt disk recovery using the last known save path.
                        await TryDiskRecoveryAsync(item, tracker, providerEntry.provider, dbContext,
                            metadataService, externalMusicSearchService, imageService, cancellationToken);
                    }
                    else if (DateTime.UtcNow - tracker.LastProgressAt > TimeSpan.FromMinutes(5))
                    {
                        Console.WriteLine($"[PendingDownloadChecker] No transfers ever seen for {item.Id} after 5min. Requeueing...");
                        await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext, cancellationToken);
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
                        await ImportAlbumAsync(item, resolvedSavePath, dbContext, metadataService,
                            externalMusicSearchService, imageService,
                            () => CleanUpCompletedDownloadAsync(providerEntry.provider, item), cancellationToken);
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
                        await HandleStalledDownloadAsync(item, tracker, providerEntry.provider, dbContext, cancellationToken);
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
        StreamingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var ownership = await CreateImportJournal(item).AcquireAsync(cancellationToken);
        if (!await dbContext.PendingDownloads.AnyAsync(current => current.Id == item.Id
            && current.ImportIdentifier == item.ImportIdentifier && current.SourceName == item.SourceName, cancellationToken)) return;
        tracker.RetryCount++;

        // Retain source files and transfer evidence while selecting a replacement.

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
        IImageService imageService,
        CancellationToken cancellationToken)
    {
        var resolvedPath = ResolveSavePath(item.SavePath ?? "");
        if (string.IsNullOrWhiteSpace(resolvedPath)) return;

        await ImportAlbumAsync(item, resolvedPath, dbContext, metadataService, externalMusicSearchService,
            imageService, () => CleanUpCompletedDownloadAsync(provider, item), cancellationToken);
    }

    private async Task CheckTorrentPendingAsync(StreamingServiceServer.Data.Models.PendingDownload item,
        IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var info = await _torrentHelper.GetTorrentInfoAsync(item.SourceName);
        if (info is null || info.Progress < 1.0) return;
        await ImportAlbumAsync(item, ResolveSavePath(info.SavePath),
            serviceProvider.GetRequiredService<StreamingDbContext>(),
            serviceProvider.GetRequiredService<IMetadataService>(),
            serviceProvider.GetRequiredService<IExternalMusicSearchService>(),
            serviceProvider.GetRequiredService<IImageService>(),
            () => CleanUpTorrentAndFilesAsync(item.SourceName), cancellationToken);
    }

    private static string GetProviderSource(StreamingServiceServer.Data.Models.PendingDownload item) =>
        item.ProviderName == "Soulseek"
            ? SlskdSourceIdentity.Parse(item.SourceName, item.SavePath)?.Encode() ?? item.SourceName : item.SourceName;

    private static async Task CleanUpCompletedDownloadAsync(IDownloadProvider provider,
        StreamingServiceServer.Data.Models.PendingDownload item)
    {
        if (provider is SoulseekProvider soulseek)
            await soulseek.TryClearCompletedTransfersAsync(GetProviderSource(item));
        else
            await provider.CleanUpAsync(GetProviderSource(item), true);
    }

    private AudioImportJournal CreateImportJournal(StreamingServiceServer.Data.Models.PendingDownload item) =>
        new(_musicLocation, item.Id, System.Text.Json.JsonSerializer.Serialize(new[] { item.ProviderName ?? "Torrent", item.SourceName, item.ImportIdentifier.ToString() }));

    private async Task<AudioImportResult> ImportAlbumAsync(StreamingServiceServer.Data.Models.PendingDownload item, string sourceDirectory,
        StreamingDbContext database, IMetadataService metadataService, IExternalMusicSearchService metadataSource,
        IImageService images, Func<Task> cleanup, CancellationToken cancellationToken)
    {
        var coordinator = new AudioAlbumImport(CreateImportJournal(item), new AudioImportFiles(_musicLocation),
            (identifier, failure) => Console.WriteLine($"[PendingDownloadChecker] Import failed for {identifier}: {failure.Message}"));
        async Task RequireCurrent(CancellationToken token)
        {
            if (!await database.PendingDownloads.AnyAsync(current => current.Id == item.Id
                && current.SourceName == item.SourceName && current.ProviderName == item.ProviderName
                && current.ImportIdentifier == item.ImportIdentifier, token))
                throw new IOException("The pending import source changed.");
        }
        var result = await coordinator.RunAsync(sourceDirectory,
            async token => (await metadataSource.SearchAlbumRecordingsByIdAsync(item.Id).WaitAsync(token))
                .Select(recording => AudioImportCatalog.CreateExpected(recording, images)).ToArray(),
            (directory, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(GetMusicFilesInSavePath(directory)); },
            async (expected, token) =>
            {
                token.ThrowIfCancellationRequested();
                await metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                var saved = await database.Recordings.Where(recording => recording.Release.Id == item.Id)
                    .Select(recording => new RecordingResponse { Id = recording.Id, ArtistName = recording.Release.Artist.Name,
                        ReleaseTitle = recording.Release.Title }).ToListAsync(token);
                AudioImportCatalog.RequireMatchingDestinations(expected, saved);
            },
            async token => { await RequireCurrent(token); await cleanup(); token.ThrowIfCancellationRequested(); },
            token => RemoveAlbumFromDatabaseQueues(item, database, token), cancellationToken, RequireCurrent);
        if (result.Complete) _stallTrackers.TryRemove(item.Id, out _);
        else Console.WriteLine($"[PendingDownloadChecker] {result.PublishedTracks}/{result.ExpectedTracks} tracks published for {item.Id}; source and queue retained.");
        return result;
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

    private async Task CleanUpTorrentAndFilesAsync(string torrentName)
    {
        var shouldTorrentClientCleanFiles = true;
        await _torrentHelper.RemoveTorrentAsync(torrentName, shouldTorrentClientCleanFiles);
    }

    private async Task RemoveAlbumFromDatabaseQueues(StreamingServiceServer.Data.Models.PendingDownload item,
        StreamingDbContext database, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var removed = await database.PendingDownloads.Where(current => current.Id == item.Id
            && current.SourceName == item.SourceName && current.ProviderName == item.ProviderName
            && current.ImportIdentifier == item.ImportIdentifier).ExecuteDeleteAsync(cancellationToken);
        if (removed != 1) throw new IOException("The pending import source changed before queue removal.");
        await database.ReleasesToDownload.Where(release => release.Id == item.Id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
