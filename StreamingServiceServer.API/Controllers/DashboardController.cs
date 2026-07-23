using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly StreamingDbContext _dbContext;
    private readonly ITorrentHelper _torrentHelper;
    private readonly IMetadataService _metadataService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DashboardController> _logger;
    private readonly DownloadProviderConfigService _providerConfigService;
    private readonly SlskdHelper _slskdHelper;
    private readonly IExternalMusicSearchService _externalMusicSearchService;
    private readonly IImageService _imageService;

    public DashboardController(
        StreamingDbContext dbContext,
        ITorrentHelper torrentHelper,
        IMetadataService metadataService,
        IConfiguration configuration,
        ILogger<DashboardController> logger,
        DownloadProviderConfigService providerConfigService,
        SlskdHelper slskdHelper,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService)
    {
        _dbContext = dbContext;
        _torrentHelper = torrentHelper;
        _metadataService = metadataService;
        _configuration = configuration;
        _logger = logger;
        _providerConfigService = providerConfigService;
        _slskdHelper = slskdHelper;
        _externalMusicSearchService = externalMusicSearchService;
        _imageService = imageService;
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        int queuedCount = 0;
        int pendingCount = 0;
        int failedCount = 0;
        int downloadedCount = 0;
        int recordingsCount = 0;

        try
        {
            queuedCount = await _dbContext.ReleasesToDownload.CountAsync(cancellationToken);
            pendingCount = await _dbContext.PendingDownloads.CountAsync(cancellationToken);
            failedCount = await _dbContext.FailedDownloads.CountAsync(cancellationToken);
            downloadedCount = await _dbContext.Releases.CountAsync(cancellationToken);
            recordingsCount = await _dbContext.Recordings.CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query DB stats, returning empty counts");
        }

        int activeTorrents = 0;
        try
        {
            activeTorrents = await _torrentHelper.GetActiveDownloadCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query qBittorrent active downloads count");
        }

        return Ok(new
        {
            queuedCount,
            pendingCount,
            failedCount,
            downloadedCount,
            recordingsCount,
            activeTorrents
        });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("queued")]
    public async Task<IActionResult> GetQueuedAlbums(CancellationToken cancellationToken)
    {
        var result = new List<object>();

        try
        {
            var releasesToDownload = await _dbContext.ReleasesToDownload.ToListAsync(cancellationToken);
            var pendingList = await _dbContext.PendingDownloads.ToListAsync(cancellationToken);
            var failedIds = (await _dbContext.FailedDownloads.Select(f => f.Id).ToListAsync(cancellationToken)).ToHashSet();

            var pendingMap = pendingList.ToDictionary(p => p.Id, p => p.SourceName);

            foreach (var rel in releasesToDownload)
            {
                string status = "queued";
                string? sourceName = null;
                double progress = 0;
                string state = "waiting";
                long size = 0;

                if (failedIds.Contains(rel.Id))
                {
                    status = "failed";
                    state = "failed";
                }
                else if (pendingMap.TryGetValue(rel.Id, out sourceName))
                {
                    status = "downloading";
                    state = "torrenting";
                    try
                    {
                        var info = await _torrentHelper.GetTorrentInfoAsync(sourceName);
                        if (info != null)
                        {
                            progress = info.Progress;
                            state = string.IsNullOrEmpty(info.State) ? "downloading" : info.State;
                            size = info.Size;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to query torrent info for {SourceName}", sourceName);
                    }
                }

                result.Add(new
                {
                    id = rel.Id,
                    title = rel.Title,
                    artist = rel.Artist,
                    status = status,
                    sourceName = sourceName,
                    progress = Math.Min(1.0, Math.Max(0.0, progress)),
                    state = state,
                    size = size
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch queued albums from DB");
        }

        return Ok(result);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("failed")]
    public async Task<IActionResult> GetFailedAlbums(CancellationToken cancellationToken)
    {
        try
        {
            var failedList = await _dbContext.FailedDownloads.ToListAsync(cancellationToken);
            var result = failedList.Select(f => new
            {
                id = f.Id,
                title = f.Title,
                artist = f.Artist,
                status = "failed",
                state = "failed"
            });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch failed albums from DB");
            return StatusCode(500, new { message = "Error fetching failed albums" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("failed/requeue-all")]
    public async Task<IActionResult> RequeueAllFailed(CancellationToken cancellationToken)
    {
        try
        {
            var failedList = await _dbContext.FailedDownloads.ToListAsync(cancellationToken);
            if (!failedList.Any())
            {
                return Ok(new { count = 0, message = "No failed downloads to requeue." });
            }

            int count = 0;
            foreach (var failed in failedList)
            {
                var existing = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == failed.Id, cancellationToken);
                if (existing == null)
                {
                    _dbContext.ReleasesToDownload.Add(new ReleaseToDownload
                    {
                        Id = failed.Id,
                        Title = failed.Title,
                        Artist = failed.Artist
                    });
                }
                _dbContext.FailedDownloads.Remove(failed);
                count++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { count, message = $"Successfully requeued {count} failed album(s)." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue all failed downloads");
            return StatusCode(500, new { message = "Error requeueing failed downloads" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("queue/requeue-all")]
    public async Task<IActionResult> RequeueAllActiveQueue(CancellationToken cancellationToken)
    {
        try
        {
            var pendingList = await _dbContext.PendingDownloads.ToListAsync(cancellationToken);
            foreach (var pending in pendingList)
            {
                try
                {
                    await _torrentHelper.RemoveTorrentAsync(pending.SourceName, deleteFiles: true);
                }
                catch { }
                _dbContext.PendingDownloads.Remove(pending);
            }

            var failedList = await _dbContext.FailedDownloads.ToListAsync(cancellationToken);
            foreach (var failed in failedList)
            {
                var existing = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == failed.Id, cancellationToken);
                if (existing == null)
                {
                    _dbContext.ReleasesToDownload.Add(new ReleaseToDownload
                    {
                        Id = failed.Id,
                        Title = failed.Title,
                        Artist = failed.Artist
                    });
                }
                _dbContext.FailedDownloads.Remove(failed);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            var totalCount = await _dbContext.ReleasesToDownload.CountAsync(cancellationToken);

            return Ok(new { count = totalCount, message = $"Reset queue. {totalCount} album(s) actively queued for download." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue all queue downloads");
            return StatusCode(500, new { message = "Error requeueing active queue downloads" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("downloaded")]
    public async Task<IActionResult> GetDownloadedAlbums([FromQuery] string? search, CancellationToken cancellationToken)
    {
        try
        {
            var query = _dbContext.Releases
                .Include(r => r.Artist)
                .Include(r => r.Recordings)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToLower();
                query = query.Where(r => r.Title.ToLower().Contains(s) || r.Artist.Name.ToLower().Contains(s));
            }

            var releases = await query
                .OrderByDescending(r => r.DateAdded)
                .Select(r => new
                {
                    id = r.Id,
                    title = r.Title ?? "Unknown Release",
                    artistName = r.Artist != null ? r.Artist.Name : "Unknown Artist",
                    cover = string.IsNullOrEmpty(r.OptimizedCover) ? (string.IsNullOrEmpty(r.Cover) ? "/api/dashboard/placeholder-cover" : r.Cover) : r.OptimizedCover,
                    recordingsCount = r.Recordings.Count,
                    dateAdded = r.DateAdded
                })
                .ToListAsync(cancellationToken);

            return Ok(releases);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch downloaded releases from DB");
            return Ok(new List<object>());
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("album/{id:guid}")]
    public async Task<IActionResult> GetAlbumDetail(Guid id, CancellationToken cancellationToken)
    {
        var downloadedRelease = await _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (downloadedRelease != null)
        {
            return Ok(new
            {
                isDownloaded = true,
                id = downloadedRelease.Id,
                title = downloadedRelease.Title ?? "Unknown Release",
                artist = downloadedRelease.Artist?.Name ?? "Unknown Artist",
                cover = string.IsNullOrEmpty(downloadedRelease.OptimizedCover) ? (string.IsNullOrEmpty(downloadedRelease.Cover) ? "/api/dashboard/placeholder-cover" : downloadedRelease.Cover) : downloadedRelease.OptimizedCover,
                dateAdded = downloadedRelease.DateAdded,
                recordings = downloadedRelease.Recordings.OrderBy(rec => rec.PositionInAlbum).Select(rec => new
                {
                    id = rec.Id,
                    title = rec.Title,
                    length = rec.Length,
                    position = rec.PositionInAlbum,
                    cover = rec.Cover
                })
            });
        }

        var queuedRelease = await _dbContext.ReleasesToDownload
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (queuedRelease != null)
        {
            var isFailed = await _dbContext.FailedDownloads.AnyAsync(f => f.Id == id, cancellationToken);
            var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            string status = isFailed ? "failed" : (pending != null ? "downloading" : "queued");
            double progress = 0;
            string state = isFailed ? "error" : (pending != null ? "active" : "queued");

            if (pending != null)
            {
                try
                {
                    var torrent = await _torrentHelper.GetTorrentInfoAsync(pending.SourceName);
                    if (torrent != null)
                    {
                        progress = torrent.Progress;
                        state = torrent.State;
                    }
                }
                catch { }
            }

            return Ok(new
            {
                isDownloaded = false,
                id = queuedRelease.Id,
                title = queuedRelease.Title,
                artist = queuedRelease.Artist,
                status = status,
                state = state,
                progress = progress,
                sourceName = pending?.SourceName
            });
        }

        return NotFound(new { message = "Album not found in library or queue." });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/add")]
    public async Task<IActionResult> AddAlbumToQueue([FromBody] AddAlbumRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Artist))
        {
            return BadRequest(new { message = "Title and Artist are required." });
        }

        string cleanTitle = request.Title.Trim();
        string cleanArtist = request.Artist.Trim();

        // 1. Fetch & persist official MusicBrainz metadata as the source of truth
        Guid releaseId = Guid.NewGuid();
        try
        {
            await _metadataService.SearchAndSaveAlbumRecordingsAsync($"{cleanArtist} {cleanTitle}");
            var mbRelease = await _dbContext.Releases
                .Include(r => r.Artist)
                .FirstOrDefaultAsync(r => r.Title.ToLower().Contains(cleanTitle.ToLower()) || 
                                          (r.Artist != null && r.Artist.Name.ToLower().Contains(cleanArtist.ToLower())), cancellationToken);
            if (mbRelease != null)
            {
                releaseId = mbRelease.Id;
                cleanTitle = mbRelease.Title ?? cleanTitle;
                cleanArtist = mbRelease.Artist?.Name ?? cleanArtist;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicBrainz lookup failed during AddAlbumToQueue for {Artist} - {Title}", cleanArtist, cleanTitle);
        }

        var releaseToDownload = new ReleaseToDownload
        {
            Id = releaseId,
            Title = cleanTitle,
            Artist = cleanArtist
        };

        var existingInQueue = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (existingInQueue == null)
        {
            _dbContext.ReleasesToDownload.Add(releaseToDownload);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { message = "Album metadata fetched from MusicBrainz and added to download queue.", id = releaseId });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/requeue")]
    public async Task<IActionResult> RequeueAlbum(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            string title = "Unknown Title";
            string artistName = "Unknown Artist";

            // 1. If album was completed/downloaded, delete files from disk and DB records
            var downloadedRelease = await _dbContext.Releases
                .Include(r => r.Artist)
                .Include(r => r.Recordings)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (downloadedRelease != null)
            {
                title = downloadedRelease.Title ?? title;
                artistName = downloadedRelease.Artist?.Name ?? artistName;

                // Delete physical audio files from disk
                var baseMusicPath = _configuration["Music:Path"] ?? _configuration["Music:DownloadPath"] ?? "./Music";
                var artistFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(artistName).ToUpper();
                var albumFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(title).ToUpper();
                var destinationDir = Path.Combine(baseMusicPath, artistFolder, albumFolder);

                if (Directory.Exists(destinationDir))
                {
                    try
                    {
                        Directory.Delete(destinationDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete physical audio files for requeued album {Id}", id);
                    }
                }

                // Remove recordings and release from DB
                if (downloadedRelease.Recordings != null && downloadedRelease.Recordings.Any())
                {
                    _dbContext.Recordings.RemoveRange(downloadedRelease.Recordings);
                }
                _dbContext.Releases.Remove(downloadedRelease);
            }

            // 2. Remove from Failed & Pending queues if present
            var failed = await _dbContext.FailedDownloads.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            if (failed != null)
            {
                title = failed.Title ?? title;
                artistName = failed.Artist ?? artistName;
                _dbContext.FailedDownloads.Remove(failed);
            }

            var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (pending != null)
            {
                try
                {
                    await _torrentHelper.RemoveTorrentAsync(pending.SourceName, deleteFiles: true);
                }
                catch { }
                _dbContext.PendingDownloads.Remove(pending);
            }

            // 3. Re-add to ReleasesToDownload for fresh scraping
            var existingToDownload = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (existingToDownload == null)
            {
                _dbContext.ReleasesToDownload.Add(new ReleaseToDownload
                {
                    Id = id,
                    Title = title,
                    Artist = artistName
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { message = "Album media deleted and re-queued for fresh download." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue album {Id}", id);
            return StatusCode(500, new { message = $"Failed to requeue album: {ex.Message}" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/replace-audio")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 536870912)] // 512 MB
    public async Task<IActionResult> ReplaceAudio(Guid id, List<IFormFile>? files, CancellationToken cancellationToken)
    {
        var fileList = files ?? Request.Form.Files.ToList();
        if (fileList == null || fileList.Count == 0)
        {
            return BadRequest(new { message = "No audio files were received in request." });
        }

        try
        {
            var baseMusicPath = _configuration["Music:Path"] ?? _configuration["Music:DownloadPath"] ?? "./Music";

            // 1. Enforce MusicBrainz as source of truth: fetch & save release metadata first
            try
            {
                await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(id);
            }
            catch { }

            var release = await _dbContext.Releases
                .Include(r => r.Artist)
                .Include(r => r.Recordings)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (release == null)
            {
                var queued = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
                if (queued != null)
                {
                    try
                    {
                        await _metadataService.SearchAndSaveAlbumRecordingsAsync($"{queued.Artist} {queued.Title}");
                    }
                    catch { }
                }

                release = await _dbContext.Releases
                    .Include(r => r.Artist)
                    .Include(r => r.Recordings)
                    .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            }

            var artistName = release?.Artist?.Name ?? "Unknown Artist";
            var releaseTitle = release?.Title ?? "Unknown Release";

            var artistFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(artistName).ToUpper();
            var albumFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(releaseTitle).ToUpper();
            var destinationDir = Path.Combine(baseMusicPath, artistFolder, albumFolder);
            Directory.CreateDirectory(destinationDir);

            var officialRecordings = release?.Recordings.OrderBy(r => r.PositionInAlbum).ToList() ?? new List<Recording>();
            int updatedCount = 0;

            foreach (var file in fileList)
            {
                if (file.Length == 0) continue;

                var fileName = Path.GetFileName(file.FileName);
                var extension = Path.GetExtension(fileName);
                var cleanFileName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

                int? fileTrackNum = null;
                var numMatch = System.Text.RegularExpressions.Regex.Match(cleanFileName, @"^(?<num>\d{1,2})\s*[-._ ]?\s*(?<title>.*)$");
                if (numMatch.Success)
                {
                    fileTrackNum = int.Parse(numMatch.Groups["num"].Value);
                }

                Recording? matchedRecording = null;
                if (fileTrackNum.HasValue && officialRecordings.Any())
                {
                    matchedRecording = officialRecordings.FirstOrDefault(r => r.PositionInAlbum == fileTrackNum.Value);
                }

                if (matchedRecording == null && officialRecordings.Any())
                {
                    int bestScore = 0;
                    foreach (var rec in officialRecordings)
                    {
                        int score = FuzzySharp.Fuzz.TokenSetRatio(cleanFileName, rec.Title.ToLowerInvariant());
                        if (score > 55 && score > bestScore)
                        {
                            bestScore = score;
                            matchedRecording = rec;
                        }
                    }
                }

                Recording targetRecording;
                if (matchedRecording != null)
                {
                    targetRecording = matchedRecording;
                }
                else
                {
                    int pos = officialRecordings.Count + 1;
                    targetRecording = new Recording
                    {
                        Id = Guid.NewGuid(),
                        Title = Path.GetFileNameWithoutExtension(fileName),
                        PositionInAlbum = pos,
                        Release = release!,
                        Cover = release?.Cover ?? string.Empty
                    };
                    _dbContext.Recordings.Add(targetRecording);
                    officialRecordings.Add(targetRecording);
                }

                var saveFileName = targetRecording.Id + extension;
                var savePath = Path.Combine(destinationDir, saveFileName);

                using (var stream = new FileStream(savePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }

                updatedCount++;
            }

            // Remove from download queues since audio files were provided manually
            var toRemoveQueued = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (toRemoveQueued != null) _dbContext.ReleasesToDownload.Remove(toRemoveQueued);

            var toRemovePending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (toRemovePending != null) _dbContext.PendingDownloads.Remove(toRemovePending);

            var toRemoveFailed = await _dbContext.FailedDownloads.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            if (toRemoveFailed != null) _dbContext.FailedDownloads.Remove(toRemoveFailed);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { message = $"Successfully matched {updatedCount} audio file(s) with MusicBrainz track metadata." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while uploading audio files for album {Id}", id);
            return StatusCode(500, new { message = $"Error uploading files: {ex.Message}" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/set-downloaded")]
    public async Task<IActionResult> SetAsDownloaded(Guid id, [FromBody] SetDownloadedRequest? request, CancellationToken cancellationToken)
    {
        var queued = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        var failed = await _dbContext.FailedDownloads.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        string title = request?.Title ?? queued?.Title ?? failed?.Title ?? "Manual Import";
        string artistName = request?.ArtistName ?? queued?.Artist ?? failed?.Artist ?? "Various Artists";

        if (queued != null) _dbContext.ReleasesToDownload.Remove(queued);
        if (pending != null) _dbContext.PendingDownloads.Remove(pending);
        if (failed != null) _dbContext.FailedDownloads.Remove(failed);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Fetch & save official metadata (MusicBrainz release info, tracks, and artwork)
        try
        {
            await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch metadata by ID {Id}", id);
        }

        var existingRelease = await _dbContext.Releases.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (existingRelease == null && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                await _metadataService.SearchAndSaveAlbumRecordingsAsync($"{artistName} {title}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch metadata by search query '{Artist} {Title}'", artistName, title);
            }
            
            existingRelease = await _dbContext.Releases.FirstOrDefaultAsync(r => r.Id == id || r.Title.ToLower() == title.ToLower(), cancellationToken);
        }

        if (existingRelease == null)
        {
            var artist = await _dbContext.Artists.FirstOrDefaultAsync(a => a.Name.ToLower() == artistName.ToLower(), cancellationToken);
            if (artist == null)
            {
                artist = new Artist { Id = Guid.NewGuid(), Name = artistName };
                _dbContext.Artists.Add(artist);
            }

            existingRelease = new Release
            {
                Id = id,
                Title = title,
                Artist = artist,
                Cover = "/api/dashboard/placeholder-cover",
                DateAdded = DateTime.UtcNow
            };
            _dbContext.Releases.Add(existingRelease);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { message = "Album metadata fetched and marked as downloaded." });
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("album/{id:guid}")]
    public async Task<IActionResult> DeleteAlbum(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var queued = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (queued != null) _dbContext.ReleasesToDownload.Remove(queued);

            var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (pending != null)
            {
                try
                {
                    await _torrentHelper.RemoveTorrentAsync(pending.SourceName, deleteFiles: true);
                }
                catch { }
                _dbContext.PendingDownloads.Remove(pending);
            }

            var failed = await _dbContext.FailedDownloads.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            if (failed != null) _dbContext.FailedDownloads.Remove(failed);

            var release = await _dbContext.Releases
                .Include(r => r.Artist)
                .Include(r => r.Recordings)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (release != null)
            {
                // Delete physical audio files from disk
                var baseMusicPath = _configuration["Music:Path"] ?? _configuration["Music:DownloadPath"] ?? "./Music";
                var artistName = release.Artist?.Name ?? "Unknown Artist";
                var releaseTitle = release.Title ?? "Unknown Release";

                var artistFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(artistName).ToUpper();
                var albumFolder = StreamingServiceServer.Business.Helpers.MusicLocationHelper.SanitizeFolderName(releaseTitle).ToUpper();
                var destinationDir = Path.Combine(baseMusicPath, artistFolder, albumFolder);

                if (Directory.Exists(destinationDir))
                {
                    try
                    {
                        Directory.Delete(destinationDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete audio directory {Dir}", destinationDir);
                    }
                }

                // Delete recordings and release from DB
                if (release.Recordings != null && release.Recordings.Any())
                {
                    _dbContext.Recordings.RemoveRange(release.Recordings);
                }
                _dbContext.Releases.Remove(release);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { message = "Album and associated audio files successfully removed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete album {Id}", id);
            return StatusCode(500, new { message = $"Failed to remove album: {ex.Message}" });
        }
    }

    [AllowAnonymous]
    [HttpGet("placeholder-cover")]
    public IActionResult GetPlaceholderCover()
    {
        string svg = @"<svg xmlns='http://www.w3.org/2000/svg' width='300' height='300' viewBox='0 0 300 300'>
          <rect width='300' height='300' fill='#E0D7CF'/>
          <circle cx='150' cy='150' r='90' fill='none' stroke='#A2978C' stroke-width='4'/>
          <circle cx='150' cy='150' r='30' fill='#A2978C'/>
          <circle cx='150' cy='150' r='8' fill='#EDE7E2'/>
        </svg>";
        return Content(svg, "image/svg+xml");
    }

    // ═══════════════════════ SETTINGS ENDPOINTS ═══════════════════════

    [Authorize(Roles = "Admin")]
    [HttpGet("settings/providers")]
    public async Task<IActionResult> GetProviderSettings(CancellationToken cancellationToken)
    {
        var configs = await _providerConfigService.GetAllConfigsAsync();
        var result = configs.Select(c => new
        {
            c.Id,
            c.ProviderName,
            c.PriorityOrder,
            c.IsEnabled,
            c.ApiUrl,
            c.Username,
            password = !string.IsNullOrWhiteSpace(c.Password) ? "••••••••" : "",
            c.ApiKey,
            c.FormatPreferences,
            c.ExtraConfig,
            c.LastUpdated
        });
        return Ok(result);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("settings/providers")]
    public async Task<IActionResult> UpdateProviderSettings(
        [FromBody] List<UpdateProviderRequest> providers, CancellationToken cancellationToken)
    {
        foreach (var p in providers)
        {
            var existing = await _dbContext.DownloadProviderConfigs
                .FirstOrDefaultAsync(c => c.Id == p.Id, cancellationToken);
            if (existing == null) continue;

            existing.PriorityOrder = p.PriorityOrder;
            existing.IsEnabled = p.IsEnabled;
            existing.ApiUrl = p.ApiUrl ?? existing.ApiUrl;
            existing.Username = p.Username ?? existing.Username;
            if (!string.IsNullOrWhiteSpace(p.Password) && p.Password != "••••••••")
                existing.Password = p.Password;
            if (p.ApiKey != null)
                existing.ApiKey = p.ApiKey;
            existing.FormatPreferences = p.FormatPreferences ?? existing.FormatPreferences;
            existing.ExtraConfig = p.ExtraConfig ?? existing.ExtraConfig;
            existing.LastUpdated = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Provider settings updated." });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("settings/providers/test")]
    public async Task<IActionResult> TestProviderConnection(
        [FromBody] TestProviderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.ProviderName.Equals("Soulseek", StringComparison.OrdinalIgnoreCase))
            {
                _slskdHelper.Configure(request.ApiUrl, request.ApiKey ?? "", request.Username ?? "", request.Password ?? "");
                var (ok, msg) = await _slskdHelper.TestConnectionDetailedAsync(cancellationToken);
                return Ok(new { success = ok, message = msg });
            }
            else if (request.ProviderName.Equals("Torrent", StringComparison.OrdinalIgnoreCase))
            {
                // Test qBittorrent connection
                await _torrentHelper.LoginAsync();
                var count = await _torrentHelper.GetActiveDownloadCountAsync();
                return Ok(new { success = true, message = $"Connected to qBittorrent. {count} active downloads." });
            }

            return BadRequest(new { success = false, message = "Unknown provider." });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, message = $"Connection failed: {ex.Message}" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("analysis")]
    public async Task<IActionResult> GetAnalysis(CancellationToken cancellationToken)
    {
        try
        {
            var results = await _dbContext.AlbumAnalysisResults
                .Include(a => a.Album)
                .ThenInclude(r => r!.Artist)
                .OrderByDescending(a => a.MissingTracks)
                .ThenBy(a => a.Album != null ? a.Album.Title : "")
                .ToListAsync(cancellationToken);

            if (results.Count == 0)
            {
                await PerformFullAnalysisScanAsync(cancellationToken);
                results = await _dbContext.AlbumAnalysisResults
                    .Include(a => a.Album)
                    .ThenInclude(r => r!.Artist)
                    .OrderByDescending(a => a.MissingTracks)
                    .ThenBy(a => a.Album != null ? a.Album.Title : "")
                    .ToListAsync(cancellationToken);
            }

            var mappedList = results.Select(r => new
            {
                albumId = r.AlbumId,
                title = r.Album?.Title ?? "Unknown",
                artistName = r.Album?.Artist?.Name ?? "Unknown Artist",
                cover = !string.IsNullOrWhiteSpace(r.Album?.LocalSmallCover) ? r.Album.LocalSmallCover : (!string.IsNullOrWhiteSpace(r.Album?.SmallCover) ? r.Album.SmallCover : r.Album?.Cover ?? ""),
                totalTracks = r.TotalTracks,
                presentTracks = r.PresentTracks,
                missingTracks = r.MissingTracks,
                lastAnalyzedAt = r.LastAnalyzedAt
            }).ToList();

            var summary = new
            {
                totalAlbums = mappedList.Count,
                completeAlbums = mappedList.Count(a => a.missingTracks == 0),
                missingAlbums = mappedList.Count(a => a.missingTracks > 0),
                totalMissingTracks = mappedList.Sum(a => a.missingTracks)
            };

            return Ok(new { success = true, summary, items = mappedList });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album analysis");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/scan")]
    public async Task<IActionResult> RescanAnalysis(CancellationToken cancellationToken)
    {
        try
        {
            await PerformFullAnalysisScanAsync(cancellationToken);
            return await GetAnalysis(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rescan album analysis");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/scan-soulseek")]
    public async Task<IActionResult> ScanSoulseekFolder(CancellationToken cancellationToken)
    {
        try
        {
            int imported = await ScanAndImportFromSoulseekAsync(null, cancellationToken);
            var results = await _dbContext.AlbumAnalysisResults
                .Include(a => a.Album)
                .ThenInclude(r => r!.Artist)
                .OrderByDescending(a => a.MissingTracks)
                .ThenBy(a => a.Album != null ? a.Album.Title : "")
                .ToListAsync(cancellationToken);

            var mappedList = results.Select(r => new
            {
                albumId = r.AlbumId,
                title = r.Album?.Title ?? "Unknown",
                artistName = r.Album?.Artist?.Name ?? "Unknown Artist",
                cover = !string.IsNullOrWhiteSpace(r.Album?.LocalSmallCover) ? r.Album.LocalSmallCover : (!string.IsNullOrWhiteSpace(r.Album?.SmallCover) ? r.Album.SmallCover : r.Album?.Cover ?? ""),
                totalTracks = r.TotalTracks,
                presentTracks = r.PresentTracks,
                missingTracks = r.MissingTracks,
                lastAnalyzedAt = r.LastAnalyzedAt
            }).ToList();

            var summary = new
            {
                totalAlbums = mappedList.Count,
                completeAlbums = mappedList.Count(a => a.missingTracks == 0),
                missingAlbums = mappedList.Count(a => a.missingTracks > 0),
                totalMissingTracks = mappedList.Sum(a => a.missingTracks)
            };

            return Ok(new { success = true, importedCount = imported, summary, items = mappedList });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan soulseek downloads folder");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/album/{id}/scan-soulseek")]
    public async Task<IActionResult> ScanSingleAlbumSoulseek(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            int imported = await ScanAndImportFromSoulseekAsync(id, cancellationToken);
            return Ok(new { success = true, importedCount = imported, message = $"Imported {imported} tracks from Soulseek downloads." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan soulseek folder for album {Id}", id);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private async Task PerformFullAnalysisScanAsync(CancellationToken cancellationToken)
    {
        var baseMusicPath = _configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        var releases = await _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .ToListAsync(cancellationToken);

        foreach (var release in releases)
        {
            var relativePath = StreamingServiceServer.Business.Helpers.MusicLocationHelper.GetRelativeFolderPathForRecording(new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
            {
                ArtistName = release.Artist?.Name ?? "",
                ReleaseTitle = release.Title ?? ""
            });

            var folderPath = Path.Combine(baseMusicPath, relativePath);
            int total = release.Recordings.Count;
            int present = 0;
            var missingIds = new List<Guid>();

            if (Directory.Exists(folderPath))
            {
                foreach (var recording in release.Recordings)
                {
                    var files = Directory.GetFiles(folderPath, recording.Id.ToString() + ".*");
                    if (files.Length > 0)
                    {
                        present++;
                    }
                    else
                    {
                        missingIds.Add(recording.Id);
                    }
                }
            }
            else
            {
                missingIds.AddRange(release.Recordings.Select(r => r.Id));
            }

            var analysis = await _dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == release.Id, cancellationToken);
            if (analysis == null)
            {
                analysis = new AlbumAnalysisResult { AlbumId = release.Id };
                _dbContext.AlbumAnalysisResults.Add(analysis);
            }

            analysis.TotalTracks = total;
            analysis.PresentTracks = present;
            analysis.MissingTracks = Math.Max(0, total - present);
            analysis.MissingRecordingIdsJson = System.Text.Json.JsonSerializer.Serialize(missingIds);
            analysis.LastAnalyzedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }


    private async Task<int> ScanAndImportFromSoulseekAsync(Guid? targetAlbumId, CancellationToken cancellationToken)
    {
        var slskdDownloadDir = _configuration["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";
        _logger.LogInformation("[Soulseek Scan] Download dir: {Dir}", slskdDownloadDir);
        if (!Directory.Exists(slskdDownloadDir))
        {
            _logger.LogWarning("[Soulseek Scan] Download dir does not exist: {Dir}", slskdDownloadDir);
            return 0;
        }

        var baseMusicPath = _configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma"
        };

        List<string> allDirectories;
        try
        {
            allDirectories = Directory.GetDirectories(slskdDownloadDir, "*", SearchOption.AllDirectories).ToList();
            allDirectories.Insert(0, slskdDownloadDir);
            _logger.LogInformation("[Soulseek Scan] Found {Count} directories under {Dir}", allDirectories.Count, slskdDownloadDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Soulseek Scan] Error listing directories");
            return 0;
        }

        var query = _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .AsQueryable();

        if (targetAlbumId.HasValue)
            query = query.Where(r => r.Id == targetAlbumId.Value);

        var releases = await query.ToListAsync(cancellationToken);
        _logger.LogInformation("[Soulseek Scan] Processing {Count} release(s)", releases.Count);
        int importedCount = 0;

        foreach (var release in releases)
        {
            var artistName = release.Artist?.Name ?? "";
            var releaseTitle = release.Title ?? "";

            _logger.LogInformation("[Soulseek Scan] --- Release: '{Title}' by '{Artist}' ({Count} recordings) ---",
                releaseTitle, artistName, release.Recordings.Count);

            var relativePath = StreamingServiceServer.Business.Helpers.MusicLocationHelper.GetRelativeFolderPathForRecording(
                new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
                {
                    ArtistName = artistName,
                    ReleaseTitle = releaseTitle
                });

            var destinationDir = Path.Combine(baseMusicPath, relativePath);
            Directory.CreateDirectory(destinationDir);

            var missingRecordings = new List<Recording>();
            foreach (var rec in release.Recordings)
            {
                var existingFiles = Directory.GetFiles(destinationDir, rec.Id.ToString() + ".*");
                if (existingFiles.Length == 0)
                    missingRecordings.Add(rec);
            }

            _logger.LogInformation("[Soulseek Scan] {Missing}/{Total} recordings missing for '{Title}'",
                missingRecordings.Count, release.Recordings.Count, releaseTitle);

            if (missingRecordings.Count == 0) continue;

            // Sanitize album title: strip brackets so "[2013] The Lost Tapes" matches "The Lost Tapes"
            string sanitizedAlbum = StringSanitizers.SanitizeTitle(
                SoulseekMatchingHelper.SanitizeFolderName(releaseTitle)).ToLowerInvariant();
            string sanitizedArtistAlbum = StringSanitizers.SanitizeTitle(
                SoulseekMatchingHelper.SanitizeFolderName($"{artistName} {releaseTitle}")).ToLowerInvariant();

            _logger.LogInformation("[Soulseek Scan] Sanitized album key: '{Album}' | artist+album: '{ArtistAlbum}'",
                sanitizedAlbum, sanitizedArtistAlbum);

            // 1. Fuzzy search all directory names (> 60%) — strip brackets from folder names before comparing
            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in allDirectories)
            {
                var rawDirName = Path.GetFileName(dir);
                string sanitizedDir = StringSanitizers.SanitizeTitle(
                    SoulseekMatchingHelper.SanitizeFolderName(rawDirName)).ToLowerInvariant();

                int albumScore = FuzzySharp.Fuzz.TokenSetRatio(sanitizedAlbum, sanitizedDir);
                int artistAlbumScore = FuzzySharp.Fuzz.TokenSetRatio(sanitizedArtistAlbum, sanitizedDir);
                int score = Math.Max(albumScore, artistAlbumScore);

                if (score > 60)
                {
                    candidateDirs.Add(dir);
                    dirScores[dir] = score;
                    _logger.LogInformation("[Soulseek Scan] Candidate folder: '{Dir}' => score {Score} (album={AlbumScore}, artist+album={ArtistScore})",
                        rawDirName, score, albumScore, artistAlbumScore);
                }
            }

            _logger.LogInformation("[Soulseek Scan] {Count} candidate folder(s) found", candidateDirs.Count);

            // Local function: match files in a folder using direct title-based matching against missingRecordings
            async Task<bool> TryImportFromCandidateFolderAsync(string rootFolder, int folderScore)
            {
                if (!Directory.Exists(rootFolder)) return false;

                var folderFiles = new List<(string fileName, string fullPath)>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(rootFolder, "*.*", SearchOption.AllDirectories))
                    {
                        if (allowedExtensions.Contains(Path.GetExtension(f)))
                            folderFiles.Add((Path.GetFileName(f), f));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Soulseek Scan] Cannot enumerate files in {Dir}", rootFolder);
                    return false;
                }

                _logger.LogInformation("[Soulseek Scan] Folder '{Folder}' (score={Score}): {Count} audio file(s)",
                    Path.GetFileName(rootFolder), folderScore, folderFiles.Count);

                if (folderFiles.Count == 0) return false;

                // Match each missing recording to a file by metadata tag or filename fuzzy score
                // NOTE: We match against our DB Recording entities directly — do NOT filter by external API IDs
                var matched = new List<(Recording recording, string fullPath, bool isMetadataMatch, int score)>();
                var remainingFiles = new List<(string fileName, string fullPath)>(folderFiles);

                foreach (var recording in missingRecordings)
                {
                    string targetTitle = recording.Title ?? "";
                    string sanitizedTarget = StringSanitizers.SanitizeTitle(targetTitle).ToLowerInvariant();

                    (string fileName, string fullPath)? bestFile = null;
                    bool isMeta = false;
                    int bestScore = 0;

                    foreach (var (fileName, fullPath) in remainingFiles)
                    {
                        var meta = AudioMetadataHelper.GetAudioMetadata(fullPath);

                        // Prefer metadata match (album+title from audio tags)
                        if (AudioMetadataHelper.IsMetadataMatch(meta, releaseTitle, targetTitle))
                        {
                            bestFile = (fileName, fullPath);
                            isMeta = true;
                            bestScore = 100;
                            break;
                        }

                        // Fallback: fuzzy on ID3 title tag if available
                        int score = 0;
                        if (meta != null && !string.IsNullOrWhiteSpace(meta.Title))
                        {
                            score = FuzzySharp.Fuzz.TokenSetRatio(
                                StringSanitizers.SanitizeTitle(meta.Title).ToLowerInvariant(),
                                sanitizedTarget);
                        }
                        else
                        {
                            // Fallback: fuzzy on filename
                            string fileNameClean = StringSanitizers.SanitizeTitle(
                                Path.GetFileNameWithoutExtension(fileName)).ToLowerInvariant();
                            score = FuzzySharp.Fuzz.TokenSetRatio(fileNameClean, sanitizedTarget);

                            if (recording.PositionInAlbum > 0)
                            {
                                string posStr = recording.PositionInAlbum.ToString();
                                string posPadded = recording.PositionInAlbum.ToString("D2");
                                if (fileName.Contains(posStr) || fileName.Contains(posPadded))
                                    score += 10;
                            }
                        }

                        if (score > bestScore && score >= 60)
                        {
                            bestScore = score;
                            bestFile = (fileName, fullPath);
                        }
                    }

                    if (bestFile != null)
                    {
                        _logger.LogInformation("[Soulseek Scan] Matched '{Track}' => '{File}' (meta={IsMeta}, score={Score})",
                            targetTitle, bestFile.Value.fileName, isMeta, bestScore);
                        matched.Add((recording, bestFile.Value.fullPath, isMeta, bestScore));
                        remainingFiles.Remove(bestFile.Value);
                    }
                    else
                    {
                        _logger.LogInformation("[Soulseek Scan] No match for '{Track}' in '{Folder}'",
                            targetTitle, Path.GetFileName(rootFolder));
                    }
                }

                int metaCount = matched.Count(m => m.isMetadataMatch);
                _logger.LogInformation("[Soulseek Scan] Folder '{Folder}': {Total} matched ({Meta} via metadata), score={FolderScore}",
                    Path.GetFileName(rootFolder), matched.Count, metaCount, folderScore);

                // Validation: if folder score >= 90, trust it unconditionally
                // Otherwise require at least 1 metadata match or >= min(3, missingCount) filename matches
                if (folderScore < 90)
                {
                    int minRequired = Math.Min(3, missingRecordings.Count);
                    bool isValid = metaCount > 0 || matched.Count >= minRequired;
                    if (!isValid)
                    {
                        _logger.LogInformation("[Soulseek Scan] Validation FAILED for '{Folder}': {Matched}/{Min} matches, {Meta} metadata",
                            Path.GetFileName(rootFolder), matched.Count, minRequired, metaCount);
                        return false;
                    }
                }

                // Move matched files into library
                int copied = 0;
                foreach (var (recording, sourcePath, _, _) in matched)
                {
                    var ext = Path.GetExtension(sourcePath);
                    var destPath = Path.Combine(destinationDir, recording.Id.ToString() + ext);
                    try
                    {
                        try
                        {
                            System.IO.File.Move(sourcePath, destPath, overwrite: true);
                        }
                        catch (IOException)
                        {
                            // Cross-device move: fall back to copy + delete
                            System.IO.File.Copy(sourcePath, destPath, overwrite: true);
                            System.IO.File.Delete(sourcePath);
                        }
                        _logger.LogInformation("[Soulseek Scan] Moved '{Source}' => '{Dest}'", sourcePath, destPath);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Soulseek Scan] Failed to move '{Source}' => '{Dest}'", sourcePath, destPath);
                    }
                }

                importedCount += copied;
                return copied > 0;
            }

            // 2. Try candidate folders (sorted by score descending)
            bool importedFromSet = false;
            var sortedCandidates = candidateDirs.OrderByDescending(d => dirScores.GetValueOrDefault(d, 0)).ToList();

            foreach (var candidateDir in sortedCandidates)
            {
                int folderScore = dirScores.GetValueOrDefault(candidateDir, 0);
                if (await TryImportFromCandidateFolderAsync(candidateDir, folderScore))
                {
                    importedFromSet = true;
                    break;
                }
            }

            // 3. If still 0 matches, fall back to ALL remaining folders
            if (!importedFromSet)
            {
                _logger.LogInformation("[Soulseek Scan] No candidate matched, falling back to all {Count} remaining dirs", 
                    allDirectories.Count - candidateDirs.Count);
                var remainingDirs = allDirectories.Where(d => !candidateDirs.Contains(d)).ToList();
                foreach (var dir in remainingDirs)
                {
                    if (await TryImportFromCandidateFolderAsync(dir, 0))
                        break;
                }
            }
        }

        await PerformFullAnalysisScanAsync(cancellationToken);
        _logger.LogInformation("[Soulseek Scan] Done. Total imported: {Count}", importedCount);
        return importedCount;
    }

}

public class AddAlbumRequest
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
}

public class SetDownloadedRequest
{
    public string? Title { get; set; }
    public string? ArtistName { get; set; }
}

public class UpdateProviderRequest
{
    public Guid Id { get; set; }
    public int PriorityOrder { get; set; }
    public bool IsEnabled { get; set; }
    public string? ApiUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string? FormatPreferences { get; set; }
    public string? ExtraConfig { get; set; }
}

public class TestProviderRequest
{
    public string ProviderName { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
}
