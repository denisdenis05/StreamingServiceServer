using StreamingServiceServer.Business.Services.Images.Assets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.Dashboard;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class DashboardController : ControllerBase
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICoverAssetStore _coverAssets;

    public DashboardController(
        StreamingDbContext dbContext,
        ITorrentHelper torrentHelper,
        IMetadataService metadataService,
        IConfiguration configuration,
        ILogger<DashboardController> logger,
        DownloadProviderConfigService providerConfigService,
        SlskdHelper slskdHelper,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService,
        IServiceScopeFactory scopeFactory,
        ICoverAssetStore coverAssets)
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
        _scopeFactory = scopeFactory;
        _coverAssets = coverAssets;
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
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to query DB stats, returning empty counts");
        }

        int activeTorrents = 0;
        try
        {
            activeTorrents = await _torrentHelper.GetActiveDownloadCountAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not query qBittorrent active downloads count");
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

            var pendingMap = pendingList.ToDictionary(p => p.Id, p => p);
            List<StreamingServiceDownloader.Models.Slskd.SlskdTransferUser>? soulseekTransfers = null;
            if (pendingList.Any(pending => pending.ProviderName == "Soulseek")
                && await ConfigureDashboardSoulseekAsync(cancellationToken))
            {
                try { soulseekTransfers = await _slskdHelper.GetDownloadsAsync(cancellationToken); }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                { _logger.LogWarning(failure, "Could not load Soulseek queue progress"); }
            }

            foreach (var rel in releasesToDownload)
            {
                string status = "queued";
                string? sourceName = null;
                string? providerName = null;
                double progress = 0;
                string state = "waiting";
                long size = 0;

                pendingMap.TryGetValue(rel.Id, out var pendingSource);
                sourceName = pendingSource?.SourceName;
                providerName = pendingSource?.ProviderName;

                if (failedIds.Contains(rel.Id))
                {
                    status = "failed";
                    state = "failed";
                }
                else if (pendingMap.TryGetValue(rel.Id, out var pendingItem))
                {
                    sourceName = pendingItem.SourceName;
                    providerName = pendingItem.ProviderName;
                    status = "downloading";
                    state = "downloading";

                    if (providerName == "Soulseek" && soulseekTransfers is not null
                        && SlskdSourceIdentity.Parse(sourceName, pendingItem.SavePath) is { } source)
                    {
                        var info = SoulseekProvider.ReadProgress(source, soulseekTransfers);
                        progress = info.Progress;
                        state = info.State;
                        size = source.Files.Sum(file => file.Size);
                    }

                    if (providerName != "Soulseek")
                    {
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
                        catch (Exception exception)
                        {
                            _logger.LogWarning(exception, "Failed to query torrent info for {SourceName}", sourceName);
                        }
                    }
                }

                result.Add(new
                {
                    id = rel.Id,
                    title = rel.Title,
                    artist = rel.Artist,
                    status = status,
                    sourceName = sourceName is null ? null : SlskdSourceIdentity.DisplayName(sourceName),
                    providerName = providerName,
                    hasDownloadSource = !string.IsNullOrWhiteSpace(sourceName),
                    progress = Math.Min(1.0, Math.Max(0.0, progress)),
                    state = state,
                    size = size
                });
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to fetch queued albums from DB");
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
            var sourcedAlbums = (await _dbContext.PendingDownloads
                .Where(pending => pending.SourceName != "").Select(pending => pending.Id)
                .ToListAsync(cancellationToken)).ToHashSet();
            var result = failedList.Select(f => new
            {
                id = f.Id,
                title = f.Title,
                artist = f.Artist,
                status = "failed",
                state = "failed",
                hasDownloadSource = sourcedAlbums.Contains(f.Id)
            });
            return Ok(result);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to fetch failed albums from DB");
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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to requeue all failed downloads");
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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to requeue all queue downloads");
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
                    cover = _imageService.GetPreferredCover(r.LocalCover, r.OptimizedCover, r.Cover),
                    recordingsCount = r.Recordings.Count,
                    dateAdded = r.DateAdded
                })
                .ToListAsync(cancellationToken);

            return Ok(releases);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to fetch downloaded releases from DB");
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
            var baseMusicPath = _configuration["Music:Path"] ?? _configuration["MusicDirectory"] ?? "/nas/media/AudioFiles/";
            var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
            {
                ReleaseTitle = downloadedRelease.Title ?? "",
                ArtistName = downloadedRelease.Artist?.Name ?? ""
            };
            System.IO.File.GetAttributes(baseMusicPath);
            var existingFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(baseMusicPath))
            {
                var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma"
                };

                foreach (var file in MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse))
                {
                    if (allowedExtensions.Contains(Path.GetExtension(file)))
                    {
                        existingFileIds.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }

            return Ok(new
            {
                isDownloaded = true,
                id = downloadedRelease.Id,
                title = downloadedRelease.Title ?? "Unknown Release",
                artist = downloadedRelease.Artist?.Name ?? "Unknown Artist",
                cover = _imageService.GetPreferredCover(downloadedRelease.LocalCover, downloadedRelease.OptimizedCover, downloadedRelease.Cover),
                dateAdded = downloadedRelease.DateAdded,
                recordings = downloadedRelease.Recordings.OrderBy(rec => rec.PositionInAlbum).Select(rec => new
                {
                    id = rec.Id,
                    title = rec.Title,
                    length = rec.Length,
                    position = rec.PositionInAlbum,
                    cover = _imageService.GetPreferredCover(rec.LocalCover, rec.OptimizedCover, rec.Cover),
                    fileExists = existingFileIds.Contains(rec.Id.ToString())
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

            if (pending?.ProviderName == "Soulseek")
            {
                if (await ConfigureDashboardSoulseekAsync(cancellationToken))
                {
                    try
                    {
                        var transfers = await _slskdHelper.GetDownloadsAsync(cancellationToken);
                        var source = SlskdSourceIdentity.Parse(pending.SourceName, pending.SavePath);
                        if (transfers is not null && source is not null)
                        {
                            var info = SoulseekProvider.ReadProgress(source, transfers);
                            progress = info.Progress;
                            state = info.State;
                        }
                    }
                    catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                    { _logger.LogWarning(failure, "Could not load Soulseek progress for album {AlbumId}", id); }
                }
            }
            else if (pending != null)
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
                sourceName = pending is null ? null : SlskdSourceIdentity.DisplayName(pending.SourceName)
            });
        }

        return NotFound(new { message = "Album not found in library or queue." });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("mb-search")]
    public async Task<IActionResult> SearchMusicBrainzAlbums([FromQuery] string? title, [FromQuery] string? artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Ok(Array.Empty<object>());

        var results = await _externalMusicSearchService.SearchAlbumsAsync(title.Trim(), artist?.Trim());
        return Ok(results.Take(8).Select(r => new
        {
            id = r.Id,
            title = r.Title ?? string.Empty,
            artist = r.ArtistCredit?.FirstOrDefault()?.Name ?? r.Artist?.Name ?? string.Empty,
        }));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("mb-search/groups")]
    public async Task<IActionResult> SearchMusicBrainzReleaseGroups([FromQuery] string? title,
        [FromQuery] string? artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return Ok(Array.Empty<object>());
        if (title.Length > 512 || artist?.Length > 512) return BadRequest("Search text is too long.");
        try
        {
            return Ok(await _externalMusicSearchService.SearchReleaseSelectionsAsync(title, artist, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "MusicBrainz search is unavailable. Try again shortly." });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("mb-search/groups/{groupId:guid}/releases")]
    public async Task<IActionResult> BrowseMusicBrainzReleaseGroup(Guid groupId, [FromQuery] int offset,
        CancellationToken cancellationToken)
    {
        if (groupId == Guid.Empty || offset < 0 || offset % 100 != 0) return BadRequest("Invalid release page.");
        try
        {
            return Ok(await _externalMusicSearchService.BrowseReleaseSelectionsAsync(groupId, offset, cancellationToken));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "MusicBrainz releases are unavailable. Try again shortly." });
        }
    }

    private static readonly HashSet<string> _audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".wma", ".opus"
    };

    private static string GetSlskdDirPath(string filename)
    {
        var lastSlash = Math.Max(filename.LastIndexOf('/'), filename.LastIndexOf('\\'));
        return lastSlash < 0 ? filename : filename[..lastSlash];
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("album/{id:guid}/slskd-sources")]
    public async Task<IActionResult> GetSlskdSources(Guid id, CancellationToken cancellationToken)
    {
        var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (pending == null || !pending.SourceName.StartsWith("slskd:", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Album is not being downloaded via slskd." });

        var currentSource = SlskdSourceIdentity.Parse(pending.SourceName, pending.SavePath);
        if (currentSource is null) return BadRequest(new { message = "Invalid slskd source identifier." });
        if (!await ConfigureDashboardSoulseekAsync(cancellationToken))
            return BadRequest(new { message = "Soulseek provider is not configured or is disabled." });
        var currentUsername = currentSource.Username;
        StreamingServiceDownloader.Models.Slskd.SlskdSearchResult? searchResult;
        try { searchResult = await _slskdHelper.GetSearchResultsAsync(currentSource.SearchId, cancellationToken); }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(failure, "Could not load slskd source choices for album {AlbumId}", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not contact slskd. Check the Soulseek provider connection settings." });
        }
        if (searchResult is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "slskd search is unavailable, expired, or access was rejected. Requeue the album if its search was removed." });
        return Ok(new { sources = BuildSlskdSourceChoices(searchResult, currentSource), currentUsername,
            importIdentifier = pending.ImportIdentifier });
    }

    private static IReadOnlyList<object> BuildSlskdSourceChoices(
        StreamingServiceDownloader.Models.Slskd.SlskdSearchResult searchResult, SlskdSourceIdentity? currentSource = null)
    {
        var rows = new List<(bool hasFreeSlots, long uploadSpeedBps, object dto)>();
        foreach (var response in (searchResult.Responses ?? []))
        {
            if (string.IsNullOrWhiteSpace(response.Username) || response.Files == null || response.Files.Count == 0)
                continue;

            var dirGroups = response.Files
                .GroupBy(f => GetSlskdDirPath(f.Filename ?? ""))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            foreach (var group in dirGroups)
            {
                var audioFiles = group
                    .Where(f => f.Filename != null && !f.IsLocked && f.Size > 0 &&
                                _audioExtensions.Contains(Path.GetExtension(f.Filename)))
                    .ToList();

                if (audioFiles.Count == 0) continue;

                var formats = audioFiles
                    .Select(f => Path.GetExtension(f.Filename ?? "").ToUpperInvariant().TrimStart('.'))
                    .Where(e => !string.IsNullOrEmpty(e))
                    .Distinct()
                    .ToList();

                var bitRates = audioFiles.Where(f => f.BitRate.HasValue).Select(f => f.BitRate!.Value).ToList();
                var avgBitRate = bitRates.Count > 0 ? (int)bitRates.Average() : 0;

                var dto = new
                {
                    username = response.Username,
                    directoryPath = group.Key,
                    fileCount = audioFiles.Count,
                    totalSizeBytes = audioFiles.Sum(f => f.Size),
                    formats,
                    avgBitRate,
                    uploadSpeedBps = response.UploadSpeed,
                    hasFreeSlots = response.FreeUploadSlots > 0,
                    freeUploadSlots = response.FreeUploadSlots,
                    isCurrentSource = currentSource is not null && response.Username == currentSource.Username
                        && SlskdSourceIdentity.NormalizePath(group.Key) == SlskdSourceIdentity.NormalizePath(currentSource.DirectoryPath),
                    files = audioFiles.Select(f => new { filename = f.Filename, size = f.Size }).ToList()
                };
                rows.Add((response.FreeUploadSlots > 0, response.UploadSpeed, dto));
            }
        }

        var sources = rows
            .OrderByDescending(r => r.hasFreeSlots)
            .ThenByDescending(r => r.uploadSpeedBps)
            .Select(r => r.dto)
            .ToList();

        return sources;
    }

    private async Task<bool> ConfigureDashboardSoulseekAsync(CancellationToken cancellationToken)
    {
        var configuration = await _dbContext.DownloadProviderConfigs.AsNoTracking()
            .FirstOrDefaultAsync(provider => provider.ProviderName == "Soulseek" && provider.IsEnabled, cancellationToken);
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.ApiUrl)) return false;
        _slskdHelper.Configure(configuration.ApiUrl, configuration.ApiKey ?? string.Empty,
            configuration.Username ?? string.Empty, configuration.Password ?? string.Empty);
        return true;
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/change-slskd-source")]
    public async Task<IActionResult> ChangeSlskdSource(Guid id, [FromBody] ChangeSlskdSourceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DirectoryPath)
            || request.Files is null || request.Files.Count == 0 || request.Files.Count > 1000)
            return BadRequest(new { message = "A source username, directory and up to 1000 files are required." });
        var snapshot = await _dbContext.PendingDownloads.AsNoTracking().SingleOrDefaultAsync(pending => pending.Id == id, cancellationToken);
        var previousSource = snapshot is null ? null : SlskdSourceIdentity.Parse(snapshot.SourceName, snapshot.SavePath);
        if (snapshot is null || snapshot.ProviderName != "Soulseek" || previousSource is null)
            return BadRequest(new { message = "Album is not pending through Soulseek." });
        if (request.ExpectedImportIdentifier != snapshot.ImportIdentifier)
            return Conflict(new { message = "The album source changed. Reload the source choices and try again." });
        if (!await ConfigureDashboardSoulseekAsync(cancellationToken))
            return BadRequest(new { message = "Soulseek provider is not configured or is disabled." });

        IAsyncDisposable ownership;
        try
        {
            var root = _configuration["Music:Path"] ?? throw new InvalidOperationException("Music storage path is not configured.");
            ownership = await new StreamingServiceDownloader.Importing.AudioImportJournal(root, id, "source-switch").AcquireAsync(cancellationToken);
        }
        catch (IOException)
        {
            return Conflict(new { message = "This album is being imported or its source is being changed. Try again shortly." });
        }
        await using var albumOwnership = ownership;
        try
        {
            var search = await _slskdHelper.GetSearchResultsAsync(previousSource.SearchId, cancellationToken);
            if (search?.Responses is null)
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "slskd search is unavailable. No source was changed." });
            var selected = search.Responses.Where(response => response.Username == request.Username)
                .SelectMany(response => response.Files ?? [])
                .Where(file => !file.IsLocked && file.Filename is not null
                    && SlskdSourceIdentity.DirectoryOf(file.Filename) == SlskdSourceIdentity.NormalizePath(request.DirectoryPath)
                    && _audioExtensions.Contains(Path.GetExtension(file.Filename)))
                .GroupBy(file => file.Filename).Select(group => group.First())
                .Select(file => new StreamingServiceDownloader.Models.Slskd.SlskdFileDownloadRequest
                    { Filename = file.Filename!, Size = file.Size }).ToArray();
            if (selected.Length == 0 || selected.Length != request.Files.Count
                || request.Files.Select(file => file.Filename).Distinct(StringComparer.Ordinal).Count() != selected.Length
                || request.Files.Any(file => !selected.Any(expected => expected.Filename == file.Filename && expected.Size == file.Size)))
                return BadRequest(new { message = "The selected files no longer match the slskd search. Reload the source choices." });

            var nextSource = new SlskdSourceIdentity(previousSource.SearchId, request.Username, request.DirectoryPath, selected);
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var pending = await _dbContext.PendingDownloads.FromSqlInterpolated(
                $"SELECT * FROM \"PendingDownloads\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (pending is null || pending.ImportIdentifier != snapshot.ImportIdentifier || pending.SourceName != snapshot.SourceName)
                return Conflict(new { message = "The pending album changed. Reload the source choices." });
            await _slskdHelper.EnsureSelectedFilesEnqueuedAsync(nextSource, cancellationToken);
            pending.SourceName = nextSource.Encode();
            pending.SavePath = request.DirectoryPath;
            pending.ImportIdentifier = Guid.NewGuid();
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            string? warning = null;
            try
            {
                var retained = nextSource.Username == previousSource.Username
                    ? selected.Select(file => SlskdSourceIdentity.NormalizePath(file.Filename)).ToHashSet(StringComparer.Ordinal) : null;
                await _slskdHelper.RemoveSelectedTransfersAsync(previousSource, cancellationToken, retained);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(failure, "Source changed for album {AlbumId}, but old slskd transfers could not all be removed", id);
                warning = "The new source is active, but some previous transfers could not be removed.";
            }
            return Ok(new { message = $"Source changed to {request.Username}. All {selected.Length} files verified in slskd.", warning });
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(failure, "Could not change slskd source for album {AlbumId}", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = failure is HttpRequestException
                ? failure.Message : "slskd timed out. Reload the source choices before retrying." });
        }
    }

    // ─── Recording Redownload ───

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RecordingRedownloadJob> _recordingJobs = new();

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{albumId:guid}/recording/{recordingId:guid}/redownload")]
    public IActionResult StartRecordingRedownload(Guid albumId, Guid recordingId)
    {
        var job = new RecordingRedownloadJob { State = "pending", StartedAt = DateTime.UtcNow };
        _recordingJobs[recordingId] = job;

        var slskd = _slskdHelper;
        var scopeFactory = _scopeFactory;
        var config = _configuration;
        var logger = _logger;

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            try
            {
                await ExecuteRecordingRedownloadAsync(recordingId, albumId, slskd, db, config, logger, CancellationToken.None);
            }
            catch (Exception ex)
            {
                var j = _recordingJobs.GetValueOrDefault(recordingId);
                if (j != null) { j.State = "error"; j.Message = ex.Message; j.CompletedAt = DateTime.UtcNow; }
                logger.LogError(ex, "[RecordingRedownload] Unhandled error for recording {Id}", recordingId);
            }
        });

        return Accepted(new { message = "Redownload started." });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("album/{albumId:guid}/recording/{recordingId:guid}/redownload/status")]
    public IActionResult GetRecordingRedownloadStatus(Guid albumId, Guid recordingId)
    {
        if (!_recordingJobs.TryGetValue(recordingId, out var job))
            return Ok(new { state = "idle" });

        return Ok(new
        {
            job.State,
            job.Message,
            job.StartedAt,
            job.CompletedAt
        });
    }

    private static async Task ExecuteRecordingRedownloadAsync(
        Guid recordingId, Guid albumId,
        StreamingServiceDownloader.Helpers.SlskdHelper slskd,
        StreamingDbContext db, IConfiguration config, ILogger logger,
        CancellationToken ct)
    {
        void SetState(string state, string? msg = null)
        {
            var j = _recordingJobs.GetValueOrDefault(recordingId);
            if (j == null) return;
            j.State = state;
            j.Message = msg;
            if (state is "complete" or "error") j.CompletedAt = DateTime.UtcNow;
        }

        SetState("loading");

        // Configure slskd with current DB settings (may not be done yet if downloader hasn't run)
        var slskdConfig = await db.DownloadProviderConfigs
            .FirstOrDefaultAsync(c => c.ProviderName == "Soulseek", ct);
        if (slskdConfig == null)
        {
            SetState("error", "Soulseek provider is not configured.");
            return;
        }
        slskd.Configure(slskdConfig.ApiUrl, slskdConfig.ApiKey ?? "", slskdConfig.Username ?? "", slskdConfig.Password ?? "");

        var recording = await db.Recordings
            .Include(r => r.Release)
            .ThenInclude(r => r!.Artist)
            .FirstOrDefaultAsync(r => r.Id == recordingId, ct);

        if (recording == null)
        {
            SetState("error", "Recording not found.");
            return;
        }

        var release = recording.Release;
        if (release == null)
        {
            SetState("error", "Release not found for recording.");
            return;
        }

        var artistName = release.Artist?.Name ?? "";
        var albumTitle = release.Title ?? "";
        var trackTitle = recording.Title ?? "";

        SetState("searching", $"Searching for \"{trackTitle}\"…");
        logger.LogInformation("[RecordingRedownload] Searching slskd for \"{Artist} {Track}\"", artistName, trackTitle);

        var searchQuery = string.IsNullOrWhiteSpace(artistName)
            ? trackTitle
            : $"{artistName} {trackTitle}";

        Guid? searchId;
        try
        {
            searchId = await slskd.SearchAsync(searchQuery, ct);
        }
        catch (Exception ex)
        {
            SetState("error", $"Search failed: {ex.Message}");
            return;
        }

        if (searchId == null)
        {
            SetState("error", "Search could not be started.");
            return;
        }

        var searchResult = await slskd.WaitForSearchResultsAsync(searchId.Value, 60, ct);
        if (searchResult?.Responses == null || searchResult.Responses.Count == 0)
        {
            await slskd.DeleteSearchAsync(searchId.Value, ct);
            SetState("error", "No results found on Soulseek.");
            return;
        }

        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".opus" };

        var normalizedTitle = System.Text.RegularExpressions.Regex
            .Replace(trackTitle.ToLowerInvariant(), @"[^\w\s]", " ").Trim();

        var candidates = new List<(string username, StreamingServiceDownloader.Models.Slskd.SlskdFileDownloadRequest file, int score)>();
        foreach (var resp in searchResult.Responses)
        {
            if (string.IsNullOrWhiteSpace(resp.Username)) continue;
            foreach (var f in resp.Files ?? new())
            {
                if (f.Filename == null || f.IsLocked) continue;
                var ext = Path.GetExtension(f.Filename);
                if (!audioExts.Contains(ext)) continue;
                if (f.Size < 1 * 1024 * 1024) continue;

                var basename = Path.GetFileNameWithoutExtension(f.Filename);
                var cleanName = System.Text.RegularExpressions.Regex
                    .Replace(basename, @"^\d{1,2}[\s._\-]+", "")
                    .Replace("_", " ").ToLowerInvariant().Trim();
                cleanName = System.Text.RegularExpressions.Regex
                    .Replace(cleanName, @"[^\w\s]", " ").Trim();

                int score = Math.Max(
                    FuzzySharp.Fuzz.TokenSetRatio(normalizedTitle, cleanName),
                    FuzzySharp.Fuzz.PartialRatio(normalizedTitle, cleanName));

                if (score < 55) continue;

                if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    score += 10;
                if (resp.FreeUploadSlots > 0) score += 5;

                candidates.Add((resp.Username, new StreamingServiceDownloader.Models.Slskd.SlskdFileDownloadRequest
                {
                    Filename = f.Filename,
                    Size = f.Size
                }, score));
            }
        }

        await slskd.DeleteSearchAsync(searchId.Value, ct);

        if (candidates.Count == 0)
        {
            SetState("error", "No matching audio file found in search results.");
            return;
        }

        var sortedCandidates = candidates.OrderByDescending(c => c.score).Take(8).ToList();
        logger.LogInformation("[RecordingRedownload] {Count} candidate(s) to try for \"{Title}\"", sortedCandidates.Count, trackTitle);

        var slskdDownloadDir = config["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";
        string? foundPath = null;
        string? foundExt = null;

        for (int ci = 0; ci < sortedCandidates.Count && foundPath == null; ci++)
        {
            var (cUsername, cFile, cScore) = sortedCandidates[ci];
            var cBasename = Path.GetFileName(cFile.Filename);
            var cExt = Path.GetExtension(cFile.Filename);

            SetState("downloading", $"Trying source {ci + 1}/{sortedCandidates.Count} ({cUsername})…");
            logger.LogInformation("[RecordingRedownload] Candidate {N}: \"{File}\" from {User} score={Score}", ci + 1, cFile.Filename, cUsername, cScore);

            try
            {
                await slskd.EnqueueDownloadAsync(cUsername, cFile, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[RecordingRedownload] Enqueue failed for {User}: {Msg}", cUsername, ex.Message);
                continue;
            }

            // Poll transfer until it resolves (complete, failed, or disappeared)
            bool definiteFail = false;
            bool transferEverSeen = false;
            var deadline = DateTime.UtcNow.AddMinutes(10);
            var appearDeadline = DateTime.UtcNow.AddMinutes(2); // how long to wait for entry to appear

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(4000, ct);
                try
                {
                    var downloads = await slskd.GetDownloadsAsync(ct);
                    var userXfers = downloads?.FirstOrDefault(d =>
                        d.Username?.Equals(cUsername, StringComparison.OrdinalIgnoreCase) == true);

                    var allXferFiles = userXfers?.Directories?
                        .SelectMany(d => d.Files ?? new List<StreamingServiceDownloader.Models.Slskd.SlskdTransferItem>())
                        .ToList() ?? new();

                    var xfer = allXferFiles.FirstOrDefault(f =>
                        f.Filename != null && f.Filename.EndsWith(cBasename, StringComparison.OrdinalIgnoreCase));

                    if (xfer == null)
                    {
                        if (transferEverSeen)
                            break; // was seen before, now gone — treat as complete, check disk

                        if (DateTime.UtcNow > appearDeadline)
                        {
                            definiteFail = true; // never showed up in slskd — silent enqueue failure
                            logger.LogWarning("[RecordingRedownload] Transfer from {User} never appeared in slskd API — skipping", cUsername);
                        }
                        continue; // not seen yet — keep waiting
                    }

                    transferEverSeen = true;

                    var xState = xfer.State ?? "";
                    if (xState.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                        xState.Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
                        break; // check disk below

                    if (xState.Contains("Errored", StringComparison.OrdinalIgnoreCase) ||
                        xState.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                        xState.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
                        xState.Contains("TimedOut", StringComparison.OrdinalIgnoreCase) ||
                        xState.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        definiteFail = true;
                        logger.LogWarning("[RecordingRedownload] Transfer from {User} state='{State}' — skipping", cUsername, xState);
                        break;
                    }
                }
                catch { }
            }

            if (definiteFail) continue;

            // Give the filesystem a moment to flush
            await Task.Delay(1500, ct);

            // Verify the file actually landed on disk — a rejection or timeout looks the same
            // as a vanished transfer entry, so disk presence is the only reliable truth
            if (Directory.Exists(slskdDownloadDir))
            {
                var matches = Directory.GetFiles(slskdDownloadDir, cBasename, SearchOption.AllDirectories);
                var hit = matches.OrderByDescending(System.IO.File.GetLastWriteTimeUtc).FirstOrDefault();
                if (hit != null)
                {
                    foundPath = hit;
                    foundExt = cExt;
                    logger.LogInformation("[RecordingRedownload] File confirmed on disk: {Path}", hit);
                }
                else
                {
                    logger.LogWarning("[RecordingRedownload] Transfer from {User} appeared to finish but file not on disk — trying next candidate", cUsername);
                }
            }
        }

        if (foundPath == null)
        {
            SetState("error", $"All {sortedCandidates.Count} source(s) failed — files were rejected, timed out, or not delivered.");
            return;
        }

        SetState("importing", "Copying to music library…");

        var musicPath = config["Music:Path"] ?? "/nas/media/AudioFiles/";
        var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
        {
            ArtistName = artistName,
            ReleaseTitle = albumTitle
        };
        var relFolder = MusicLocationHelper.GetRelativeFolderPathForRecording(recResponse);
        var destDir = Path.Combine(musicPath, relFolder);
        Directory.CreateDirectory(destDir);

        var destFile = Path.Combine(destDir, recordingId + foundExt);

        try
        {
            System.IO.File.Copy(foundPath, destFile, overwrite: true);
            logger.LogInformation("[RecordingRedownload] Copied {Src} → {Dst}", foundPath, destFile);
        }
        catch (Exception ex)
        {
            SetState("error", $"Failed to copy file to library: {ex.Message}");
            return;
        }

        SetState("complete", $"Track saved: {Path.GetFileName(destFile)}");
        logger.LogInformation("[RecordingRedownload] Complete for recording {Id}", recordingId);
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


        Guid releaseId = request.MbId ?? Guid.NewGuid();
        try
        {
            if (request.MbId.HasValue)
            {
                await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(request.MbId.Value);
                var mbRelease = await _dbContext.Releases
                    .Include(r => r.Artist)
                    .FirstOrDefaultAsync(r => r.Id == request.MbId.Value, cancellationToken);
                if (mbRelease != null)
                {
                    cleanTitle = mbRelease.Title ?? cleanTitle;
                    cleanArtist = mbRelease.Artist?.Name ?? cleanArtist;
                }
            }
            else
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
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "MusicBrainz lookup failed during AddAlbumToQueue for {Artist} - {Title}", cleanArtist, cleanTitle);
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


            var downloadedRelease = await _dbContext.Releases
                .Include(r => r.Artist)
                .Include(r => r.Recordings)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (downloadedRelease != null)
            {
                title = downloadedRelease.Title ?? title;
                artistName = downloadedRelease.Artist?.Name ?? artistName;


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
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to delete physical audio files for requeued album {Id}", id);
                    }
                }


                if (downloadedRelease.Recordings != null && downloadedRelease.Recordings.Any())
                {
                    _dbContext.Recordings.RemoveRange(downloadedRelease.Recordings);
                }
                _dbContext.Releases.Remove(downloadedRelease);
            }


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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to requeue album {Id}", id);
            return StatusCode(500, new { message = $"Failed to requeue album: {exception.Message}" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/replace-audio")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 536870912)] 
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

            var destinationDir = Path.Combine(baseMusicPath,
                MusicLocationHelper.GetRelativeFolderPathForRecording(artistName, releaseTitle));
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


            var toRemoveQueued = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (toRemoveQueued != null) _dbContext.ReleasesToDownload.Remove(toRemoveQueued);

            var toRemovePending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (toRemovePending != null) _dbContext.PendingDownloads.Remove(toRemovePending);

            var toRemoveFailed = await _dbContext.FailedDownloads.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            if (toRemoveFailed != null) _dbContext.FailedDownloads.Remove(toRemoveFailed);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { message = $"Successfully matched {updatedCount} audio file(s) with MusicBrainz track metadata." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error occurred while uploading audio files for album {Id}", id);
            return StatusCode(500, new { message = $"Error uploading files: {exception.Message}" });
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


        try
        {
            await _metadataService.SearchAndSaveAlbumRecordingsByIdAsync(id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not fetch metadata by ID {Id}", id);
        }

        var existingRelease = await _dbContext.Releases.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (existingRelease == null && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                await _metadataService.SearchAndSaveAlbumRecordingsAsync($"{artistName} {title}");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not fetch metadata by search query '{Artist} {Title}'", artistName, title);
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
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to delete audio directory {Dir}", destinationDir);
                    }
                }


                if (release.Recordings != null && release.Recordings.Any())
                {
                    _dbContext.Recordings.RemoveRange(release.Recordings);
                }
                _dbContext.Releases.Remove(release);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { message = "Album and associated audio files successfully removed." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete album {Id}", id);
            return StatusCode(500, new { message = $"Failed to remove album: {exception.Message}" });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/add-manual")]
    public async Task<IActionResult> AddManualAlbum([FromBody] AddManualAlbumRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.ArtistName))
            return BadRequest(new { message = "Title and ArtistName are required." });

        var artist = await _dbContext.Artists
            .FirstOrDefaultAsync(a => a.Name.ToLower() == request.ArtistName.ToLower().Trim(), cancellationToken);

        if (artist == null)
        {
            artist = new Artist { Id = Guid.NewGuid(), Name = request.ArtistName.Trim() };
            _dbContext.Artists.Add(artist);
        }

        var release = new Release
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Artist = artist,
            Cover = "/api/dashboard/placeholder-cover",
            DateAdded = DateTime.UtcNow
        };

        _dbContext.Releases.Add(release);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Album created.", id = release.Id });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/recording/add")]
    public async Task<IActionResult> AddRecording(Guid id, [FromBody] AddRecordingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "Title is required." });

        if (request.PositionInAlbum < 1)
            return BadRequest(new { message = "PositionInAlbum must be >= 1." });

        var release = await _dbContext.Releases
            .Include(r => r.Artist)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (release == null)
            return NotFound(new { message = "Album not found." });

        var recording = new Recording
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            PositionInAlbum = request.PositionInAlbum,
            Length = request.LengthMs,
            Release = release,
            Cover = release.Cover ?? string.Empty,
            SmallCover = release.SmallCover,
            VerySmallCover = release.VerySmallCover,
            OptimizedCover = release.OptimizedCover ?? string.Empty,
            OptimizedSmallCover = release.OptimizedSmallCover ?? string.Empty,
            OptimizedVerySmallCover = release.OptimizedVerySmallCover ?? string.Empty,
            LocalCover = release.LocalCover ?? string.Empty,
            LocalSmallCover = release.LocalSmallCover ?? string.Empty,
            LocalVerySmallCover = release.LocalVerySmallCover ?? string.Empty,
            DateAdded = DateTime.UtcNow
        };

        _dbContext.Recordings.Add(recording);

        var analysis = await _dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == id, cancellationToken);
        if (analysis != null) _dbContext.AlbumAnalysisResults.Remove(analysis);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Track added.", id = recording.Id, title = recording.Title, position = recording.PositionInAlbum });
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("album/{id:guid}/recording/{recordingId:guid}")]
    public async Task<IActionResult> DeleteRecording(Guid id, Guid recordingId, CancellationToken cancellationToken)
    {
        var recording = await _dbContext.Recordings
            .Include(r => r.Release)
                .ThenInclude(rel => rel.Artist)
            .FirstOrDefaultAsync(r => r.Id == recordingId && r.Release.Id == id, cancellationToken);

        if (recording == null)
            return NotFound(new { message = "Recording not found on this album." });

        var baseMusicPath = _configuration["Music:Path"] ?? _configuration["MusicDirectory"] ?? "/nas/media/AudioFiles/";
        var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
        {
            ReleaseTitle = recording.Release?.Title ?? "",
            ArtistName = recording.Release?.Artist?.Name ?? ""
        };
        if (Directory.Exists(baseMusicPath))
        {
            var audioFile = MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse)
                .FirstOrDefault(f => string.Equals(
                    Path.GetFileNameWithoutExtension(f), recordingId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (audioFile != null)
            {
                try { System.IO.File.Delete(audioFile); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete audio file for recording {Id}", recordingId); }
            }
        }

        _dbContext.Recordings.Remove(recording);

        var analysis = await _dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == id, cancellationToken);
        if (analysis != null) _dbContext.AlbumAnalysisResults.Remove(analysis);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Track deleted." });
    }

    [Authorize(Roles = "Admin")]
    [HttpDelete("album/{id:guid}/recordings/missing")]
    public async Task<IActionResult> DeleteMissingRecordings(Guid id, CancellationToken cancellationToken)
    {
        var release = await _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (release == null)
            return NotFound(new { message = "Album not found." });

        var baseMusicPath = _configuration["Music:Path"] ?? _configuration["MusicDirectory"] ?? "/nas/media/AudioFiles/";
        var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
        {
            ReleaseTitle = release.Title ?? "",
            ArtistName = release.Artist?.Name ?? ""
        };
        System.IO.File.GetAttributes(baseMusicPath);
        var existingFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(baseMusicPath))
        {
            var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma" };
            foreach (var file in MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse))
                if (audioExts.Contains(Path.GetExtension(file)))
                    existingFileIds.Add(Path.GetFileNameWithoutExtension(file));
        }

        var missing = release.Recordings
            .Where(r => !existingFileIds.Contains(r.Id.ToString()))
            .ToList();

        if (missing.Count == 0)
            return Ok(new { removed = 0, message = "No missing-file tracks found." });

        _dbContext.Recordings.RemoveRange(missing);

        var analysis = await _dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == id, cancellationToken);
        if (analysis != null) _dbContext.AlbumAnalysisResults.Remove(analysis);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { removed = missing.Count, message = $"Removed {missing.Count} track(s) with no audio file." });
    }

    private static readonly HashSet<string> _allowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    private static readonly Dictionary<string, string> _imageTypeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp"
    };

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/update-cover")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 10485760)]
    public async Task<IActionResult> UpdateAlbumCover(Guid id, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No image file received." });

        var contentType = file.ContentType?.ToLowerInvariant() ?? "";
        if (!_allowedImageTypes.Contains(contentType))
            return BadRequest(new { message = "Only JPEG, PNG, and WebP images are accepted." });

        var release = await _dbContext.Releases
            .Include(r => r.Recordings)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (release == null)
            return NotFound(new { message = "Album not found." });

        var previousCover = release.Cover;
        CoverAssetSet assets;
        try
        {
            await using var input = file.OpenReadStream();
            assets = await _coverAssets.ImportAsync(input, cancellationToken);
        }
        catch (IOException) { return BadRequest(new { message = "The cover could not be decoded or exceeds the configured image limits." }); }
        var publicUrl = _coverAssets.PublicAddress(assets.Full);
        var smallUrl = _coverAssets.PublicAddress(assets.Small);
        var thumbnailUrl = _coverAssets.PublicAddress(assets.Thumbnail);
        await using var publication = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await _dbContext.Releases.Where(current => current.Id == id && current.Cover == previousCover)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Cover, publicUrl), cancellationToken);
        if (claimed == 0) return Conflict(new { message = "The album cover changed while this upload was being processed. Reload and try again." });
        release.Cover = release.OptimizedCover = release.LocalCover = publicUrl;
        release.SmallCover = release.OptimizedSmallCover = release.LocalSmallCover = smallUrl;
        release.VerySmallCover = release.OptimizedVerySmallCover = release.LocalVerySmallCover = thumbnailUrl;
        foreach (var recording in release.Recordings)
        {
            recording.Cover = recording.OptimizedCover = recording.LocalCover = publicUrl;
            recording.SmallCover = recording.OptimizedSmallCover = recording.LocalSmallCover = smallUrl;
            recording.VerySmallCover = recording.OptimizedVerySmallCover = recording.LocalVerySmallCover = thumbnailUrl;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await publication.CommitAsync(cancellationToken);

        return Ok(new { message = "Cover updated.", coverUrl = publicUrl, smallCoverUrl = smallUrl, verySmallCoverUrl = thumbnailUrl });
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

                await _torrentHelper.LoginAsync();
                var count = await _torrentHelper.GetActiveDownloadCountAsync();
                return Ok(new { success = true, message = $"Connected to qBittorrent. {count} active downloads." });
            }

            return BadRequest(new { success = false, message = "Unknown provider." });
        }
        catch (Exception exception)
        {
            return Ok(new { success = false, message = $"Connection failed: {exception.Message}" });
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
                await PerformFullAnalysisScanAsync(_dbContext, _configuration, cancellationToken);
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
                lastAnalyzedAt = r.LastAnalyzedAt,
                mismatchedTracksCount = r.MismatchedTracksCount,
                lastMismatchCheckedAt = r.LastMismatchCheckedAt
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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to get album analysis");
            return StatusCode(500, new { success = false, message = exception.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/scan")]
    public async Task<IActionResult> RescanAnalysis(CancellationToken cancellationToken)
    {
        try
        {
            await PerformFullAnalysisScanAsync(_dbContext, _configuration, cancellationToken);
            return await GetAnalysis(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to rescan album analysis");
            return StatusCode(500, new { success = false, message = exception.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/requeue-fully-missing")]
    public async Task<IActionResult> RequeueFullyMissingAlbums(CancellationToken cancellationToken)
    {
        try
        {
            var fullyMissingIds = await _dbContext.AlbumAnalysisResults
                .Where(a => a.PresentTracks == 0 && a.TotalTracks > 0)
                .Select(a => a.AlbumId)
                .ToListAsync(cancellationToken);

            if (fullyMissingIds.Count == 0)
            {
                return Ok(new { success = true, count = 0, message = "No fully missing albums found to requeue." });
            }

            var releases = await _dbContext.Releases
                .Include(r => r.Artist)
                .Where(r => fullyMissingIds.Contains(r.Id))
                .ToListAsync(cancellationToken);

            int requeuedCount = 0;
            foreach (var release in releases)
            {
                var pending = await _dbContext.PendingDownloads.FirstOrDefaultAsync(p => p.Id == release.Id, cancellationToken);
                if (pending != null)
                {
                    _dbContext.PendingDownloads.Remove(pending);
                }

                var existingToDownload = await _dbContext.ReleasesToDownload.FirstOrDefaultAsync(r => r.Id == release.Id, cancellationToken);
                if (existingToDownload == null)
                {
                    _dbContext.ReleasesToDownload.Add(new ReleaseToDownload
                    {
                        Id = release.Id,
                        Title = release.Title ?? "",
                        Artist = release.Artist?.Name ?? ""
                    });
                    requeuedCount++;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("[Analysis] Requeued {Count} fully missing album(s) to download queue", requeuedCount);
            return Ok(new { success = true, count = requeuedCount, message = $"Requeued {requeuedCount} fully missing album(s) for download." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to requeue fully missing albums");
            return StatusCode(500, new { success = false, message = exception.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("analysis/scan-soulseek/status")]
    public IActionResult GetSoulseekScanStatus()
    {
        return Ok(SoulseekScanStatus.Current);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/scan-soulseek")]
    public IActionResult ScanSoulseekFolder()
    {
        if (SoulseekScanStatus.IsRunning)
            return Ok(new { success = true, message = "Scan already in progress.", status = SoulseekScanStatus.Current });

        var scopeFactory = _scopeFactory;
        var config = _configuration;
        var logger = _logger;


        _ = Task.Run(async () =>
        {
            SoulseekScanStatus.Start();
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var musicSearch = scope.ServiceProvider.GetRequiredService<IExternalMusicSearchService>();
            var imageService = scope.ServiceProvider.GetRequiredService<IImageService>();
            var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            try
            {
                int imported = await ScanAndImportFromSoulseekAsync(null, CancellationToken.None, db, musicSearch, imageService, metadataService, config, logger);
                SoulseekScanStatus.Complete(imported);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[Soulseek Scan] Background scan failed");
                SoulseekScanStatus.Fail(exception.Message);
            }
        });

        return Accepted(new { success = true, message = "Soulseek scan started in background. Poll /api/dashboard/analysis/scan-soulseek/status for results.", status = SoulseekScanStatus.Current });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/album/{id}/scan-soulseek")]
    public IActionResult ScanSingleAlbumSoulseek(Guid id)
    {
        if (SoulseekScanStatus.IsRunning)
            return Ok(new { success = true, message = "Scan already in progress.", status = SoulseekScanStatus.Current });

        var scopeFactory = _scopeFactory;
        var config = _configuration;
        var logger = _logger;

        _ = Task.Run(async () =>
        {
            SoulseekScanStatus.Start();
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var musicSearch = scope.ServiceProvider.GetRequiredService<IExternalMusicSearchService>();
            var imageService = scope.ServiceProvider.GetRequiredService<IImageService>();
            var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            try
            {
                int imported = await ScanAndImportFromSoulseekAsync(id, CancellationToken.None, db, musicSearch, imageService, metadataService, config, logger);
                SoulseekScanStatus.Complete(imported);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[Soulseek Scan] Background scan failed for album {Id}", id);
                SoulseekScanStatus.Fail(exception.Message);
            }
        });

        return Accepted(new { success = true, message = $"Soulseek scan started for album {id}. Poll /api/dashboard/analysis/scan-soulseek/status for results.", status = SoulseekScanStatus.Current });
    }

    // ─── Batch Mismatch Redownload ───

    private class BatchRedownloadStatus
    {
        public string State { get; set; } = "pending";
        public string? Message { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, BatchRedownloadStatus> _batchJobs = new();

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/redownload-mismatched")]
    public IActionResult StartBatchMismatchRedownload(Guid id, [FromBody] BatchMismatchRedownloadRequest request)
    {
        if (request.RecordingIds == null || request.RecordingIds.Count == 0)
            return BadRequest(new { message = "No recording IDs provided." });

        var job = new BatchRedownloadStatus { State = "pending", StartedAt = DateTime.UtcNow, Total = request.RecordingIds.Count };
        _batchJobs[id] = job;

        var slskd = _slskdHelper;
        var scopeFactory = _scopeFactory;
        var config = _configuration;
        var logger = _logger;
        var ids = request.RecordingIds.ToList();

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            try
            {
                await ExecuteBatchMismatchRedownloadAsync(id, ids, slskd, db, config, logger, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (_batchJobs.TryGetValue(id, out var j)) { j.State = "error"; j.Message = ex.Message; j.CompletedAt = DateTime.UtcNow; }
                logger.LogError(ex, "[BatchRedownload] Unhandled error for album {Id}", id);
            }
        });

        return Accepted(new { message = "Batch redownload started." });
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("album/{id:guid}/redownload-mismatched/status")]
    public IActionResult GetBatchMismatchRedownloadStatus(Guid id)
    {
        if (!_batchJobs.TryGetValue(id, out var job))
            return Ok(new { state = "idle" });

        return Ok(new { job.State, job.Message, job.Done, job.Total, job.StartedAt, job.CompletedAt });
    }

    private static async Task ExecuteBatchMismatchRedownloadAsync(
        Guid albumId,
        List<Guid> targetRecordingIds,
        StreamingServiceDownloader.Helpers.SlskdHelper slskd,
        StreamingDbContext db,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        void SetState(string state, string? msg = null, int done = 0, int total = 0)
        {
            if (!_batchJobs.TryGetValue(albumId, out var j)) return;
            j.State = state; j.Message = msg; j.Done = done; j.Total = total;
            if (state is "complete" or "error") j.CompletedAt = DateTime.UtcNow;
        }

        SetState("loading");

        var slskdConfig = await db.DownloadProviderConfigs.FirstOrDefaultAsync(c => c.ProviderName == "Soulseek", ct);
        if (slskdConfig == null) { SetState("error", "Soulseek provider not configured."); return; }
        slskd.Configure(slskdConfig.ApiUrl, slskdConfig.ApiKey ?? "", slskdConfig.Username ?? "", slskdConfig.Password ?? "");

        var release = await db.Releases
            .Include(r => r.Artist).Include(r => r.Recordings)
            .FirstOrDefaultAsync(r => r.Id == albumId, ct);
        if (release == null) { SetState("error", "Album not found."); return; }

        var targetRecordings = release.Recordings
            .Where(r => targetRecordingIds.Contains(r.Id))
            .OrderBy(r => r.PositionInAlbum)
            .ToList();

        if (targetRecordings.Count == 0) { SetState("error", "No target recordings found."); return; }

        var artistName = release.Artist?.Name ?? "";
        var albumTitle = release.Title ?? "";
        var searchQuery = string.IsNullOrWhiteSpace(artistName) ? albumTitle : $"{artistName} {albumTitle}";

        SetState("searching", $"Searching Soulseek for \"{albumTitle}\"…", 0, targetRecordings.Count);
        logger.LogInformation("[BatchRedownload] Search query: \"{Query}\"", searchQuery);

        Guid? searchId;
        try { searchId = await slskd.SearchAsync(searchQuery, ct); }
        catch (Exception ex) { SetState("error", $"Search failed: {ex.Message}"); return; }
        if (searchId == null) { SetState("error", "Could not start search."); return; }

        var searchResult = await slskd.WaitForSearchResultsAsync(searchId.Value, 60, ct);
        await slskd.DeleteSearchAsync(searchId.Value, ct);

        if (searchResult?.Responses == null || searchResult.Responses.Count == 0)
        {
            SetState("error", "No Soulseek results found."); return;
        }

        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".opus" };

        var sanitizedAlbum = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(
            SoulseekMatchingHelper.SanitizeFolderName(albumTitle)).ToLowerInvariant();

        // Collect and score candidates (one per user+directory)
        var candidates = new List<(string Username, List<StreamingServiceDownloader.Models.Slskd.SlskdFile> Files, int DirScore, bool HasFreeSlots, long UploadSpeed)>();
        foreach (var resp in searchResult.Responses)
        {
            if (string.IsNullOrWhiteSpace(resp.Username)) continue;
            var audioFiles = (resp.Files ?? new())
                .Where(f => f.Filename != null && !f.IsLocked && audioExts.Contains(Path.GetExtension(f.Filename)))
                .GroupBy(f => GetSlskdDirPath(f.Filename!));

            foreach (var grp in audioFiles)
            {
                var files = grp.ToList();
                if (files.Count == 0) continue;

                var dirName = Path.GetFileName(grp.Key.TrimEnd('/', '\\'));
                var sanitizedDir = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(
                    SoulseekMatchingHelper.SanitizeFolderName(dirName)).ToLowerInvariant();

                int dirScore = Math.Max(
                    FuzzySharp.Fuzz.TokenSetRatio(sanitizedAlbum, sanitizedDir),
                    FuzzySharp.Fuzz.PartialRatio(sanitizedAlbum, sanitizedDir));

                candidates.Add((resp.Username, files, dirScore, resp.FreeUploadSlots > 0, resp.UploadSpeed));
            }
        }

        var sorted = candidates
            .OrderByDescending(c => c.DirScore)
            .ThenByDescending(c => c.HasFreeSlots)
            .ThenByDescending(c => c.UploadSpeed)
            .ToList();

        var musicPath  = config["Music:Path"] ?? "/nas/media/AudioFiles/";
        var downloadDir = config["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";

        var recResponseForPath = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
            { ArtistName = artistName, ReleaseTitle = albumTitle };
        var destDir = Path.Combine(musicPath, MusicLocationHelper.GetRelativeFolderPathForRecording(recResponseForPath));
        Directory.CreateDirectory(destDir);

        var recResponses = targetRecordings.Select(r => new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
        {
            Id = r.Id, Title = r.Title, PositionInAlbum = r.PositionInAlbum,
            ReleaseTitle = albumTitle, ArtistName = artistName
        }).ToList();

        int totalDownloaded = 0;

        foreach (var candidate in sorted)
        {
            // fullPath = slskd remote filename; MatchSongsWithRecordings uses filename for scoring
            var fileList = candidate.Files
                .Select(f => (Path.GetFileName(f.Filename!), f.Filename!))
                .ToList();

            var matchResults = SoulseekMatchingHelper.MatchSongsWithRecordings(recResponses, fileList);
            var validMatches = matchResults.Where(m => m.Score >= 50 && !string.IsNullOrWhiteSpace(m.FullPath)).ToList();

            if (validMatches.Count == 0) continue;

            logger.LogInformation("[BatchRedownload] {User}: {Count} match(es), dir score {Score}",
                candidate.Username, validMatches.Count, candidate.DirScore);

            var fileLookup = candidate.Files.ToDictionary(f => f.Filename!, StringComparer.OrdinalIgnoreCase);
            var downloadRequests = validMatches
                .Where(m => fileLookup.ContainsKey(m.FullPath))
                .Select(m => new StreamingServiceDownloader.Models.Slskd.SlskdFileDownloadRequest
                    { Filename = m.FullPath, Size = fileLookup[m.FullPath].Size })
                .ToList();

            SetState("downloading", $"Queuing {downloadRequests.Count} track(s) from {candidate.Username}…", 0, validMatches.Count);

            try { await slskd.EnqueueDirectoryDownloadAsync(candidate.Username, downloadRequests, ct); }
            catch (Exception ex)
            {
                logger.LogWarning("[BatchRedownload] Enqueue failed for {User}: {Msg}", candidate.Username, ex.Message);
                continue;
            }

            // Poll disk until each file arrives, then copy to library
            var pending = validMatches.Select(m => (
                Match: m,
                Recording: targetRecordings.First(r => r.Id == m.Recording.Id),
                BaseName: Path.GetFileName(m.FullPath),
                Ext: Path.GetExtension(m.FullPath)
            )).ToList();

            var deadline = DateTime.UtcNow.AddMinutes(15);
            int done = 0;

            while (pending.Count > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(4000, ct);

                var stillPending = new List<(MusicFileMatch, Recording, string, string)>();
                foreach (var (match, recording, baseName, ext) in pending)
                {
                    string? hit = null;
                    if (Directory.Exists(downloadDir))
                        hit = Directory.GetFiles(downloadDir, baseName, SearchOption.AllDirectories)
                            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc).FirstOrDefault();

                    if (hit == null) { stillPending.Add((match, recording, baseName, ext)); continue; }

                    var destFile = Path.Combine(destDir, recording.Id + ext);
                    try
                    {
                        System.IO.File.Copy(hit, destFile, overwrite: true);
                        done++;
                        totalDownloaded++;
                        SetState("downloading", $"{done}/{validMatches.Count} track(s) copied", done, validMatches.Count);
                        logger.LogInformation("[BatchRedownload] {Src} → {Dst}", hit, destFile);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[BatchRedownload] Copy failed: {File}", hit);
                        stillPending.Add((match, recording, baseName, ext));
                    }
                }
                pending = stillPending;
            }

            if (totalDownloaded > 0) break;
        }

        if (totalDownloaded > 0)
            SetState("complete", $"Replaced {totalDownloaded} track(s) successfully.", totalDownloaded, totalDownloaded);
        else
            SetState("error", "No matching tracks could be downloaded from any source.");
    }

    // ─── Track Match Check Infrastructure ───

    private record TrackIssue(string Type, string Severity, string Detail);

    // Each entry is a named checker: (filePath, recording, release, metadata) → issues list.
    // Add new entries here to extend the suite with additional checks.
    private static readonly List<(string Name, Func<string, Recording, Release, StreamingServiceDownloader.Models.AudioMetadata?, List<TrackIssue>> Fn)> _trackCheckers = new()
    {
        ("metadata_name", CheckMetadataName),
        ("track_length",  CheckTrackLength),
    };

    private static List<TrackIssue> CheckMetadataName(string filePath, Recording rec, Release release, StreamingServiceDownloader.Models.AudioMetadata? meta)
    {
        var issues = new List<TrackIssue>();
        if (meta == null || string.IsNullOrWhiteSpace(meta.Title)) return issues;

        var sanitizedDb   = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(rec.Title).ToLowerInvariant();
        var sanitizedFile = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(meta.Title).ToLowerInvariant();

        int titleScore = Math.Max(
            FuzzySharp.Fuzz.PartialRatio(sanitizedDb, sanitizedFile),
            FuzzySharp.Fuzz.TokenSetRatio(sanitizedDb, sanitizedFile));

        if (titleScore < 70)
        {
            string severity = titleScore < 50 ? "error" : "warning";
            issues.Add(new TrackIssue("name_mismatch", severity,
                $"Tag: \"{meta.Title}\" — DB: \"{rec.Title}\""));
        }

        if (!string.IsNullOrWhiteSpace(meta.Artist))
        {
            var artistName = release.Artist?.Name ?? "";
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                var sanitizedDbArtist   = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(artistName).ToLowerInvariant();
                var sanitizedFileArtist = StreamingServiceDownloader.Helpers.StringSanitizers.SanitizeTitle(meta.Artist).ToLowerInvariant();

                int artistScore = Math.Max(
                    FuzzySharp.Fuzz.PartialRatio(sanitizedDbArtist, sanitizedFileArtist),
                    FuzzySharp.Fuzz.TokenSetRatio(sanitizedDbArtist, sanitizedFileArtist));

                if (artistScore < 65)
                    issues.Add(new TrackIssue("artist_mismatch", "warning",
                        $"Tag artist: \"{meta.Artist}\" — DB: \"{artistName}\""));
            }
        }

        return issues;
    }

    private static List<TrackIssue> CheckTrackLength(string filePath, Recording rec, Release release, StreamingServiceDownloader.Models.AudioMetadata? meta)
    {
        var issues = new List<TrackIssue>();
        if (meta?.DurationSeconds == null || rec.Length == null) return issues;

        double dbDurationSeconds = rec.Length.Value / 1000.0;
        double diff = Math.Abs(meta.DurationSeconds.Value - dbDurationSeconds);
        if (diff <= 1.0) return issues;

        string fileFmt = FormatTrackSeconds(meta.DurationSeconds.Value);
        string dbFmt   = FormatTrackSeconds(dbDurationSeconds);
        string diffFmt = $"{(int)Math.Round(diff)}s";

        string severity = diff <= 5.0 ? "warning" : "error";
        string note     = diff <= 5.0 ? " — may be normal" : "";
        issues.Add(new TrackIssue("length_mismatch", severity,
            $"File: {fileFmt}, DB: {dbFmt} ({diffFmt}{note})"));

        return issues;
    }

    private static string FormatTrackSeconds(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static readonly HashSet<string> _audioExtsCheck = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma", ".opus" };

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/check-track-matches")]
    public async Task<IActionResult> CheckAlbumTrackMatches(Guid id, CancellationToken cancellationToken)
    {
        var release = await _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (release == null)
            return NotFound(new { message = "Album not found." });

        var baseMusicPath = _configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
        {
            ReleaseTitle = release.Title ?? "",
            ArtistName   = release.Artist?.Name ?? ""
        };
        var filesByRecId = new Dictionary<Guid, string>();
        if (Directory.Exists(baseMusicPath))
        {
            foreach (var file in MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse))
            {
                if (!_audioExtsCheck.Contains(Path.GetExtension(file))) continue;
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var rid))
                    filesByRecId.TryAdd(rid, file);
            }
        }

        var tracks = new List<object>();
        int issueCount = 0;
        int checkedFiles = 0;

        foreach (var recording in release.Recordings.OrderBy(r => r.PositionInAlbum))
        {
            bool fileExists = filesByRecId.TryGetValue(recording.Id, out var filePath);

            if (!fileExists)
            {
                tracks.Add(new { recordingId = recording.Id, title = recording.Title, position = recording.PositionInAlbum, fileExists = false, hasMetadata = false, hasIssues = false, issues = Array.Empty<object>() });
                continue;
            }

            checkedFiles++;
            var meta = StreamingServiceDownloader.Helpers.AudioMetadataHelper.GetAudioMetadata(filePath!);
            var issues = new List<TrackIssue>();
            foreach (var (_, fn) in _trackCheckers)
                issues.AddRange(fn(filePath!, recording, release, meta));

            if (issues.Count > 0) issueCount++;

            tracks.Add(new
            {
                recordingId = recording.Id,
                title       = recording.Title,
                position    = recording.PositionInAlbum,
                fileExists  = true,
                hasMetadata = meta != null,
                hasIssues   = issues.Count > 0,
                issues      = issues.Select(i => new { i.Type, i.Severity, i.Detail }).ToList()
            });
        }

        return Ok(new
        {
            albumId = id,
            checkedFiles,
            issueCount,
            tracks
        });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("analysis/check-mismatches")]
    public async Task<IActionResult> CheckAllMismatches(CancellationToken cancellationToken)
    {
        var releases = await _dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .ToListAsync(cancellationToken);

        var baseMusicPath = _configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        int albumsWithIssues = 0;
        int totalIssues = 0;

        foreach (var release in releases)
        {
            var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
            {
                ReleaseTitle = release.Title ?? "",
                ArtistName   = release.Artist?.Name ?? ""
            };
            var filesByRecId = new Dictionary<Guid, string>();
            if (Directory.Exists(baseMusicPath))
            {
                foreach (var file in MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse))
                {
                    if (!_audioExtsCheck.Contains(Path.GetExtension(file))) continue;
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var rid))
                        filesByRecId.TryAdd(rid, file);
                }
            }

            int mismatchCount = 0;
            var trackIssuesList = new List<object>();

            foreach (var recording in release.Recordings.OrderBy(r => r.PositionInAlbum))
            {
                if (!filesByRecId.TryGetValue(recording.Id, out var filePath)) continue;

                var meta = StreamingServiceDownloader.Helpers.AudioMetadataHelper.GetAudioMetadata(filePath);
                var issues = new List<TrackIssue>();
                foreach (var (_, fn) in _trackCheckers)
                    issues.AddRange(fn(filePath, recording, release, meta));

                if (issues.Count > 0)
                {
                    mismatchCount++;
                    totalIssues += issues.Count;
                    trackIssuesList.Add(new
                    {
                        recordingId = recording.Id,
                        title       = recording.Title,
                        position    = recording.PositionInAlbum,
                        issues      = issues.Select(i => new { i.Type, i.Severity, i.Detail }).ToList()
                    });
                }
            }

            var analysis = await _dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == release.Id, cancellationToken);
            if (analysis == null)
            {
                analysis = new AlbumAnalysisResult { AlbumId = release.Id };
                _dbContext.AlbumAnalysisResults.Add(analysis);
            }
            analysis.MismatchedTracksCount  = mismatchCount;
            analysis.TrackMatchIssuesJson   = System.Text.Json.JsonSerializer.Serialize(trackIssuesList);
            analysis.LastMismatchCheckedAt  = DateTime.UtcNow;

            if (mismatchCount > 0) albumsWithIssues++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success          = true,
            totalAlbums      = releases.Count,
            albumsWithIssues,
            totalIssues
        });
    }

    private static async Task PerformFullAnalysisScanAsync(
        StreamingDbContext dbContext,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var baseMusicPath = configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        var releases = await dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .ToListAsync(cancellationToken);

        foreach (var release in releases)
        {
            var recResponse = new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
            {
                ArtistName = release.Artist?.Name ?? "",
                ReleaseTitle = release.Title ?? ""
            };
            int total = release.Recordings.Count;
            int present = 0;
            var missingIds = new List<Guid>();

            if (Directory.Exists(baseMusicPath))
            {
                var existingBasenames = MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath, recResponse)
                    .Select(Path.GetFileNameWithoutExtension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var recording in release.Recordings)
                {
                    if (existingBasenames.Contains(recording.Id.ToString()))
                        present++;
                    else
                        missingIds.Add(recording.Id);
                }
            }
            else
            {
                missingIds.AddRange(release.Recordings.Select(r => r.Id));
            }

            var analysis = await dbContext.AlbumAnalysisResults.FirstOrDefaultAsync(a => a.AlbumId == release.Id, cancellationToken);
            if (analysis == null)
            {
                analysis = new AlbumAnalysisResult { AlbumId = release.Id };
                dbContext.AlbumAnalysisResults.Add(analysis);
            }

            analysis.TotalTracks = total;
            analysis.PresentTracks = present;
            analysis.MissingTracks = Math.Max(0, total - present);
            analysis.MissingRecordingIdsJson = System.Text.Json.JsonSerializer.Serialize(missingIds);
            analysis.LastAnalyzedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }


    private static async Task<int> ScanAndImportFromSoulseekAsync(
        Guid? targetAlbumId,
        CancellationToken cancellationToken,
        StreamingDbContext dbContext,
        IExternalMusicSearchService externalMusicSearchService,
        IImageService imageService,
        IMetadataService metadataService,
        IConfiguration configuration,
        ILogger logger)
    {
        var slskdDownloadDir = configuration["Music:DownloadPath"] ?? "/nas/slskd-data/downloads";
        logger.LogInformation("[Soulseek Scan] Download dir: {Dir}", slskdDownloadDir);
        if (!Directory.Exists(slskdDownloadDir))
        {
            logger.LogWarning("[Soulseek Scan] Download dir does not exist: {Dir}", slskdDownloadDir);
            return 0;
        }

        var baseMusicPath = configuration["Music:Path"] ?? "/nas/media/AudioFiles/";
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma"
        };

        List<string> allDirectories;
        try
        {
            allDirectories = Directory.GetDirectories(slskdDownloadDir, "*", SearchOption.AllDirectories).ToList();
            allDirectories.Insert(0, slskdDownloadDir);
            logger.LogInformation("[Soulseek Scan] Found {Count} directories under {Dir}", allDirectories.Count, slskdDownloadDir);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[Soulseek Scan] Error listing directories");
            return 0;
        }

        var query = dbContext.Releases
            .Include(r => r.Artist)
            .Include(r => r.Recordings)
            .AsQueryable();

        if (targetAlbumId.HasValue)
            query = query.Where(r => r.Id == targetAlbumId.Value);

        var releases = await query.ToListAsync(cancellationToken);
        logger.LogInformation("[Soulseek Scan] Processing {Count} release(s)", releases.Count);
        int importedCount = 0;

        foreach (var release in releases)
        {
            var artistName = release.Artist?.Name ?? "";
            var releaseTitle = release.Title ?? "";

            logger.LogInformation("[Soulseek Scan] --- Release: '{Title}' by '{Artist}' ({Count} recordings) ---",
                releaseTitle, artistName, release.Recordings.Count);

            var relativePath = StreamingServiceServer.Business.Helpers.MusicLocationHelper.GetRelativeFolderPathForRecording(
                new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
                {
                    ArtistName = artistName,
                    ReleaseTitle = releaseTitle
                });

            var destinationDir = Path.Combine(baseMusicPath, relativePath);
            Directory.CreateDirectory(destinationDir);

            var existingBasenames = MusicLocationHelper.EnumerateRecordingFiles(baseMusicPath,
                    new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
                    {
                        ArtistName = artistName,
                        ReleaseTitle = releaseTitle
                    })
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingRecordings = release.Recordings
                .Where(rec => !existingBasenames.Contains(rec.Id.ToString()))
                .ToList();

            logger.LogInformation("[Soulseek Scan] {Missing}/{Total} recordings missing for '{Title}'",
                missingRecordings.Count, release.Recordings.Count, releaseTitle);

            if (missingRecordings.Count == 0) continue;

            string albumClean = SoulseekMatchingHelper.SanitizeFolderName(releaseTitle).ToLowerInvariant();
            string artistAlbumClean = SoulseekMatchingHelper.SanitizeFolderName($"{artistName} {releaseTitle}").ToLowerInvariant();
            string sanitizedAlbum = StringSanitizers.SanitizeTitle(albumClean).ToLowerInvariant();
            string sanitizedArtistAlbum = StringSanitizers.SanitizeTitle(artistAlbumClean).ToLowerInvariant();

            logger.LogInformation("[Soulseek Scan] Album keys: clean='{AlbumClean}', sanitized='{SanitizedAlbum}'",
                albumClean, sanitizedAlbum);


            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in allDirectories)
            {
                var rawDirName = Path.GetFileName(dir.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(rawDirName)) continue;

                var relativeDirName = Path.GetRelativePath(slskdDownloadDir, dir);

                string folderClean = SoulseekMatchingHelper.SanitizeFolderName(rawDirName).ToLowerInvariant();
                string relFolderClean = SoulseekMatchingHelper.SanitizeFolderName(relativeDirName).ToLowerInvariant();

                string sanitizedDir = StringSanitizers.SanitizeTitle(folderClean).ToLowerInvariant();
                string sanitizedRelDir = StringSanitizers.SanitizeTitle(relFolderClean).ToLowerInvariant();

                int score = 0;

                if (folderClean == albumClean || folderClean == artistAlbumClean ||
                    relFolderClean == artistAlbumClean || relFolderClean.EndsWith(artistAlbumClean) || relFolderClean.Contains(artistAlbumClean) ||
                    (!string.IsNullOrWhiteSpace(albumClean) && albumClean.Length >= 4 && (folderClean.Contains(albumClean) || relFolderClean.Contains(albumClean))))
                {
                    score = 100;
                }
                else
                {
                    int albumScore = FuzzySharp.Fuzz.TokenSetRatio(sanitizedAlbum, sanitizedDir);
                    int artistAlbumScore = FuzzySharp.Fuzz.TokenSetRatio(sanitizedArtistAlbum, sanitizedRelDir);
                    int partialScore = Math.Max(
                        FuzzySharp.Fuzz.PartialRatio(sanitizedAlbum, sanitizedDir),
                        FuzzySharp.Fuzz.PartialRatio(sanitizedArtistAlbum, sanitizedRelDir)
                    );
                    score = Math.Max(Math.Max(albumScore, artistAlbumScore), partialScore);
                }

                if (score > 60)
                {
                    candidateDirs.Add(dir);
                    dirScores[dir] = score;
                    logger.LogInformation("[Soulseek Scan] Candidate folder: '{Dir}' (rel: '{RelDir}') => score {Score}",
                        rawDirName, relativeDirName, score);
                }
            }

            logger.LogInformation("[Soulseek Scan] {Count} candidate folder(s) found", candidateDirs.Count);

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
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "[Soulseek Scan] Cannot enumerate files in {Dir}", rootFolder);
                    return false;
                }

                logger.LogInformation("[Soulseek Scan] Folder '{Folder}' (score={Score}): {Count} audio file(s)",
                    Path.GetFileName(rootFolder), folderScore, folderFiles.Count);

                if (folderFiles.Count == 0) return false;

                var recordingResponses = missingRecordings.Select(r => new StreamingServiceServer.Business.Models.MusicSearch.RecordingResponse
                {
                    Id = r.Id,
                    Title = r.Title,
                    PositionInAlbum = r.PositionInAlbum,
                    ReleaseTitle = releaseTitle,
                    ArtistName = artistName
                }).ToList();

                var matchResults = folderScore > 85
                    ? SoulseekMatchingHelper.MatchSongsByFilenameCombinations(recordingResponses, folderFiles)
                    : SoulseekMatchingHelper.MatchSongsWithRecordings(recordingResponses, folderFiles);

                var validMatches = matchResults
                    .Where(m => m.Score >= 50 && !string.IsNullOrWhiteSpace(m.FullPath))
                    .ToList();

                int metaCount = validMatches.Count(m =>
                {
                    var meta = AudioMetadataHelper.GetAudioMetadata(m.FullPath);
                    return AudioMetadataHelper.IsMetadataMatch(meta, releaseTitle, m.Recording.Title ?? "");
                });

                logger.LogInformation("[Soulseek Scan] Folder '{Folder}': {Total} matched ({Meta} via metadata), score={FolderScore}",
                    Path.GetFileName(rootFolder), validMatches.Count, metaCount, folderScore);

                if (folderScore < 90)
                {
                    int minRequired = Math.Min(3, missingRecordings.Count);
                    bool isValid = metaCount > 0 || validMatches.Count >= minRequired;
                    if (!isValid)
                    {
                        logger.LogInformation("[Soulseek Scan] Validation FAILED for '{Folder}': {Matched}/{Min} matches, {Meta} metadata",
                            Path.GetFileName(rootFolder), validMatches.Count, minRequired, metaCount);
                        return false;
                    }
                }

                int moved = 0;
                foreach (var match in validMatches)
                {
                    var ext = Path.GetExtension(match.FullPath);
                    var destPath = Path.Combine(destinationDir, match.Recording.Id.ToString() + ext);
                    try
                    {
                        bool imported = false;
                        try
                        {
                            System.IO.File.Move(match.FullPath, destPath, overwrite: true);
                            imported = true;
                        }
                        catch (Exception moveException)
                        {
                            logger.LogWarning("[Soulseek Scan] Move failed for '{Source}' ({Msg}). Fallback to Copy...",
                                match.FullPath, moveException.Message);

                            System.IO.File.Copy(match.FullPath, destPath, overwrite: true);
                            imported = true;

                            try
                            {
                                System.IO.File.Delete(match.FullPath);
                            }
                            catch (Exception deleteException)
                            {
                                logger.LogWarning("[Soulseek Scan] Source delete skipped after copy: {Msg}", deleteException.Message);
                            }
                        }

                        if (imported)
                        {
                            logger.LogInformation("[Soulseek Scan] Successfully imported '{Source}' => '{Dest}'", match.FullPath, destPath);
                            moved++;
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "[Soulseek Scan] Failed to import '{Source}' => '{Dest}'", match.FullPath, destPath);
                    }
                }

                importedCount += moved;
                return moved > 0;
            }


            var sortedCandidates = candidateDirs.OrderByDescending(d => dirScores.GetValueOrDefault(d, 0)).ToList();

            foreach (var candidateDir in sortedCandidates)
            {
                int folderScore = dirScores.GetValueOrDefault(candidateDir, 0);
                if (await TryImportFromCandidateFolderAsync(candidateDir, folderScore))
                {
                    break;
                }
            }
        }

        await PerformFullAnalysisScanAsync(dbContext, configuration, cancellationToken);
        logger.LogInformation("[Soulseek Scan] Done. Total imported: {Count}", importedCount);
        return importedCount;
    }
}

