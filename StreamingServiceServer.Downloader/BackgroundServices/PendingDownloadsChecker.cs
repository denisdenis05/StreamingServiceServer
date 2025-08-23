using System.Net;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;
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
    private string _musicLocation;

    public PendingDownloadChecker(StreamingDbContext dbContext, ITorrentHelper torrentHelper, IExternalMusicSearchService externalMusicSearchService, IMetadataService metadataService, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _torrentHelper = torrentHelper;
        _externalMusicSearchService = externalMusicSearchService;
        _metadataService = metadataService;
        _configuration = configuration;
        
        _musicLocation = _configuration["Music:Path"];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingDownloadsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PendingDownloadChecker] Error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task CheckPendingDownloadsAsync()
    {
        await Task.Delay(3000);
        var pending = _dbContext.PendingDownloads.ToList();

        foreach (var item in pending)
        {
            try
            {
                var info = await _torrentHelper.GetTorrentInfoAsync(item.SourceName);
                if (info != null && info.Progress >= 1.0)
                {
                    var files = GetMusicFilesInSavePath(info.SavePath);
                    var matches = await MatchSongsWithMetadataAsync(item.Id, files);

                    MoveMatchedFiles(matches);
                    await CleanUpTorrentAndFilesAsync(item.SourceName);
                    await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(item.Id);

                    await RemoveAlbumFromDatabaseQueues(item.Id);
                }
            }
            catch
            {
                Console.WriteLine($"[PendingDownloadChecker] Error: {item.Id}");
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
        var simpleRecordings = recordings.Select(recording => recording.ToResponse()).ToList();
    
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
    
    private void MoveMatchedFiles(ICollection<MusicFileMatch> matches)
    {
        var baseMusicPath = _musicLocation;

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to move {match.FileName}: {ex.Message}");
            }
        }
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