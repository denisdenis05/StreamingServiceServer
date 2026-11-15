using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Importing;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceServer.Business.Models.Dashboard;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.API.Controllers;

public partial class DashboardController
{
    private const int MaximumSourceQueryLength = 200;
    private const int SourceSearchTimeoutSeconds = 90;

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/slskd-source-search")]
    public async Task<IActionResult> SearchSlskdSources(Guid id, [FromBody] SlskdSourceSearchRequest request,
        CancellationToken cancellationToken)
    {
        var release = await _dbContext.ReleasesToDownload.AsNoTracking()
            .SingleOrDefaultAsync(album => album.Id == id, cancellationToken);
        var failed = await _dbContext.FailedDownloads.AsNoTracking()
            .SingleOrDefaultAsync(album => album.Id == id, cancellationToken);
        if (release is null && failed is null)
            return NotFound(new { message = "This album is no longer in the download queue." });
        var pending = await _dbContext.PendingDownloads.AsNoTracking()
            .SingleOrDefaultAsync(album => album.Id == id, cancellationToken);
        if (pending is not null && !string.IsNullOrWhiteSpace(pending.SourceName))
            return Conflict(new { message = "This album already has a download source. Refresh the queue and use change source." });
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            query = $"{release?.Artist ?? failed!.Artist} {release?.Title ?? failed!.Title}".Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumSourceQueryLength)
            return BadRequest(new { message = $"Enter search terms of up to {MaximumSourceQueryLength} characters." });
        if (!await ConfigureDashboardSoulseekAsync(cancellationToken))
            return BadRequest(new { message = "Soulseek provider is not configured or is disabled." });
        try
        {
            var searchIdentifier = await _slskdHelper.SearchAsync(query, cancellationToken);
            if (searchIdentifier is null)
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "slskd could not start the search." });
            var search = await _slskdHelper.WaitForSearchResultsAsync(searchIdentifier.Value,
                SourceSearchTimeoutSeconds, cancellationToken);
            if (search is null)
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Could not read slskd search results. Try searching again." });
            return Ok(new { sources = BuildSlskdSourceChoices(search), searchIdentifier, query,
                importIdentifier = pending?.ImportIdentifier ?? Guid.Empty });
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(failure, "Could not search for a manual source for album {AlbumId}", id);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "Could not search Soulseek. Check the provider connection and try again." });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("album/{id:guid}/add-slskd-source")]
    public async Task<IActionResult> AddSlskdSource(Guid id, [FromBody] AddSlskdSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SearchIdentifier == Guid.Empty || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.DirectoryPath) || request.Files is null
            || request.Files.Count == 0 || request.Files.Count > SlskdSourceSelection.MaximumFiles
            || request.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Filename) || file.Size <= 0))
            return BadRequest(new { message = "Select a source from a Soulseek search with a username, directory and audio files." });
        if (!await ConfigureDashboardSoulseekAsync(cancellationToken))
            return BadRequest(new { message = "Soulseek provider is not configured or is disabled." });
        IAsyncDisposable ownership;
        try
        {
            var root = _configuration["Music:Path"] ?? throw new InvalidOperationException("Music storage path is not configured.");
            ownership = await new AudioImportJournal(root, id, "manual-source").AcquireAsync(cancellationToken);
        }
        catch (IOException)
        {
            return Conflict(new { message = "A download search, import or source change is already running for this album. Try again shortly." });
        }
        await using var albumOwnership = ownership;
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var release = await _dbContext.ReleasesToDownload.FromSqlInterpolated(
                $"SELECT * FROM \"ReleasesToDownload\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            var failed = await _dbContext.FailedDownloads.FromSqlInterpolated(
                $"SELECT * FROM \"FailedDownloads\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (release is null && failed is null)
                return NotFound(new { message = "This album is no longer in the download queue." });
            var pending = await _dbContext.PendingDownloads.FromSqlInterpolated(
                $"SELECT * FROM \"PendingDownloads\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if ((pending is not null && !string.IsNullOrWhiteSpace(pending.SourceName))
                || (pending?.ImportIdentifier ?? Guid.Empty) != request.ExpectedImportIdentifier)
                return Conflict(new { message = "The album source changed. Refresh the queue before selecting a source." });
            var search = await _slskdHelper.GetSearchResultsAsync(request.SearchIdentifier, cancellationToken);
            if (search is null)
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "The Soulseek search is unavailable or expired. Search again before selecting a source." });
            var selected = SlskdSourceSelection.GetFiles(search, request.Username, request.DirectoryPath);
            var requested = request.Files.Select(file => new SlskdFileDownloadRequest
                { Filename = file.Filename, Size = file.Size }).ToArray();
            if (!SlskdSourceSelection.MatchesFiles(selected, requested))
                return BadRequest(new { message = "The selected files no longer match the Soulseek search. Search again." });
            var source = new SlskdSourceIdentity(request.SearchIdentifier, request.Username, request.DirectoryPath, selected);
            await _slskdHelper.EnsureSelectedFilesEnqueuedAsync(source, cancellationToken);
            if (release is null)
                _dbContext.ReleasesToDownload.Add(new ReleaseToDownload
                    { Id = id, Title = failed!.Title, Artist = failed.Artist });
            if (pending is null)
            {
                pending = new PendingDownload { Id = id };
                _dbContext.PendingDownloads.Add(pending);
            }
            pending.SourceName = source.Encode();
            pending.ProviderName = "Soulseek";
            pending.SavePath = request.DirectoryPath;
            pending.ImportIdentifier = Guid.NewGuid();
            if (failed is not null) _dbContext.FailedDownloads.Remove(failed);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Ok(new { message = $"Source added from {request.Username}. The album is queued with all {selected.Length} files verified in slskd." });
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(failure, "Could not add a manual source for album {AlbumId}", id);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = failure is HttpRequestException
                ? failure.Message : "slskd timed out. The album's queue state was retained; try again." });
        }
    }
}
