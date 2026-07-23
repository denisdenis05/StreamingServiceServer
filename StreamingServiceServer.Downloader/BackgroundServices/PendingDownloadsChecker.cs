using System.Net;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly StreamingDbContext _dbContext;
    private readonly ITorrentHelper _torrentHelper;
    private readonly IExternalMusicSearchService _externalMusicSearchService;
    private readonly IMetadataService _metadataService;
    private readonly IConfiguration _configuration;
    private readonly IImageService _imageService;
    private readonly DownloadProviderConfigService _configService;
    private string _musicLocation;

    public PendingDownloadChecker(
        StreamingDbContext dbContext, 
        ITorrentHelper torrentHelper, 
        IExternalMusicSearchService externalMusicSearchService, 
        IMetadataService metadataService, 
        IConfiguration configuration, 
        IImageService imageService,
        DownloadProviderConfigService configService)
    {
        _dbContext = dbContext;
        _torrentHelper = torrentHelper;
        _externalMusicSearchService = externalMusicSearchService;
        _metadataService = metadataService;
        _configuration = configuration;
        _imageService = imageService;
        _configService = configService;
        
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
        var pending = _dbContext.PendingDownloads.ToList();

        // Get providers so we can route by ProviderName
        var providers = await _configService.GetActiveProvidersOrderedAsync();

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
                    await CheckTorrentPendingAsync(item);
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

                        var matches = await MatchSongsWithMetadataAsync(item.Id, files);
                        var movedCount = MoveMatchedFiles(matches);

                        if (movedCount > 0)
                        {
                            // Save MusicBrainz metadata
                            await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                            await RemoveAlbumFromDatabaseQueues(item.Id);
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
    private async Task CheckTorrentPendingAsync(StreamingServiceServer.Data.Models.PendingDownload item)
    {
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

            var matches = await MatchSongsWithMetadataAsync(item.Id, files);
            var movedCount = MoveMatchedFiles(matches);

            if (movedCount > 0)
            {
                await CleanUpTorrentAndFilesAsync(item.SourceName);
                await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);
                await RemoveAlbumFromDatabaseQueues(item.Id);
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

    private async Task<ICollection<MusicFileMatch>> MatchSongsWithMetadataAsync(
        Guid albumId, 
        List<(string fileName, string fullPath)> files)
    {
        var recordings = await _externalMusicSearchService.SearchAlbumRecordingsByIdAsync(albumId);
        var simpleRecordings = recordings.Select(recording => recording.ToResponse(_imageService)).ToList();
    
        var matches = new List<MusicFileMatch>();
    
        var unmatchedFiles = new List<(string fileName, string fullPath)>(files);
    
        foreach (var recording in simpleRecordings.OrderBy(r => r.PositionInAlbum))
        {
            string target = $"{recording.PositionInAlbum} {recording.Title}";
            string sanitizedTarget = StringSanitizers.SanitizeTitle(target).ToLower();
    
            (string fileName, string fullPath)? bestFile = null;
            int bestScore = 0;
    
            foreach (var (fileName, fullPath) in unmatchedFiles)
            {
                var metadata = GetAudioMetadata(fullPath);

                int combinedScore = 0;

                if (metadata != null && !string.IsNullOrWhiteSpace(metadata.Title))
                {
                    int titleScore = Fuzz.TokenSetRatio(
                        StringSanitizers.SanitizeTitle(metadata.Title).ToLower(),
                        sanitizedTarget
                    );

                    combinedScore = titleScore;
                }
                else
                {
                    var (fileTrackNum, fileTitle) = ExtractTrackInfo(fileName);

                    bool trackNumMatches = fileTrackNum.HasValue &&
                                           fileTrackNum == recording.PositionInAlbum;

                    int titleScore = Fuzz.TokenSetRatio(fileTitle, sanitizedTarget);

                    combinedScore = titleScore;
                    if (trackNumMatches)
                        combinedScore += 15; 
                }

                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestFile = (fileName, fullPath);
                }
            }
    
            if (bestFile != null)
            {
                matches.Add(new MusicFileMatch
                {
                    FileName = bestFile.Value.fileName,
                    FullPath = bestFile.Value.fullPath,
                    Recording = recording,
                    Score = bestScore
                });
    
                unmatchedFiles.Remove(bestFile.Value);
            }
            else
            {
                matches.Add(new MusicFileMatch
                {
                    FileName = "<NO MATCH>",
                    FullPath = string.Empty,
                    Recording = recording,
                    Score = 0
                });
            }
        }
    
        return matches;
    }
    
    private static string SanitizeFileNameTitle(string title)
    {
        title = Path.GetFileNameWithoutExtension(title);
        return title.ToLowerInvariant();
    }
    
    private static (int? trackNumber, string cleanTitle) ExtractTrackInfo(string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        var match = System.Text.RegularExpressions.Regex.Match(
            nameWithoutExt,
            @"^(?<num>\d{1,2})\s*[-._ ]?\s*(?<title>.*)$"
        );

        if (match.Success)
        {
            int num = int.Parse(match.Groups["num"].Value);
            string title = match.Groups["title"].Value;
            title = System.Text.RegularExpressions.Regex.Replace(title, @"[^\w\s]", " ");
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ").Trim();
            return (num, title.ToLowerInvariant());
        }
        else
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(nameWithoutExt, @"[^\w\s]", " ");
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
            return (null, clean.ToLowerInvariant());
        }
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

    private async Task RemoveAlbumFromDatabaseQueues(Guid albumId)
    {
        var releaseToDownload = await _dbContext.ReleasesToDownload
            .FirstOrDefaultAsync(r => r.Id == albumId);
        if (releaseToDownload != null)
        {
            _dbContext.ReleasesToDownload.Remove(releaseToDownload);
        }

        var pendingDownload = await _dbContext.PendingDownloads
            .FirstOrDefaultAsync(p => p.Id == albumId);
        if (pendingDownload != null)
        {
            _dbContext.PendingDownloads.Remove(pendingDownload);
        }

        await _dbContext.SaveChangesAsync();
    }
    
    private AudioMetadata? GetAudioMetadata(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            return new AudioMetadata
            {
                Title = file.Tag.Title,
                Artist = file.Tag.FirstPerformer,
                Album = file.Tag.Album,
                TrackNumber = file.Tag.Track,
                Year = file.Tag.Year
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read metadata: {ex.Message}");
            return null;
        }
    }
}