using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamingServiceDownloader.Helpers;
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

    public DashboardController(
        StreamingDbContext dbContext,
        ITorrentHelper torrentHelper,
        IMetadataService metadataService,
        IConfiguration configuration,
        ILogger<DashboardController> logger,
        DownloadProviderConfigService providerConfigService,
        SlskdHelper slskdHelper)
    {
        _dbContext = dbContext;
        _torrentHelper = torrentHelper;
        _metadataService = metadataService;
        _configuration = configuration;
        _logger = logger;
        _providerConfigService = providerConfigService;
        _slskdHelper = slskdHelper;
    }

    [AllowAnonymous]
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

    [AllowAnonymous]
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

    [AllowAnonymous]
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

    [AllowAnonymous]
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

    [AllowAnonymous]
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

    [AllowAnonymous]
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
