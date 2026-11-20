using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Lyrics;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.API.Controllers;

public static class LyricsRoutes
{
    public const string Recordings = "api/lyrics";
    public const string Recording = "{recordingIdentifier:guid}";
    public const string Download = Recording + "/download";
    public const string Queue = "api/dashboard/lyrics";
    public const string Administrator = "Admin";
    public const string TextMediaType = "text/plain; charset=utf-8";
    public const string SyncedExtension = ".lrc";
    public const string PlainExtension = ".txt";
    public const string LyricsfileExtension = ".yaml";
    public const string UnsupportedFormat = "Unknown lyrics format.";
    public const string UnsupportedState = "Unknown lyrics state.";
}

public enum LyricsExportFormat { Synced, Plain, Lyricsfile }

[ApiController]
[Authorize]
[Route(LyricsRoutes.Recordings)]
public sealed class LyricsController(LyricsReader reader) : ControllerBase
{
    [HttpGet(LyricsRoutes.Recording)]
    public async Task<ActionResult<LyricsResponse>> Get(Guid recordingIdentifier, CancellationToken cancellationToken)
    {
        var response = await reader.ReadAsync(recordingIdentifier, cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet(LyricsRoutes.Download)]
    public async Task<IActionResult> Download(Guid recordingIdentifier, [FromQuery] LyricsExportFormat format = LyricsExportFormat.Synced,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) return BadRequest(LyricsRoutes.UnsupportedFormat);
        var response = await reader.ReadAsync(recordingIdentifier, cancellationToken);
        var text = format == LyricsExportFormat.Synced ? response?.SyncedLyrics
            : format == LyricsExportFormat.Plain ? response?.PlainLyrics : response?.Lyricsfile;
        if (string.IsNullOrWhiteSpace(text)) return NotFound();
        var extension = format == LyricsExportFormat.Synced ? LyricsRoutes.SyncedExtension
            : format == LyricsExportFormat.Plain ? LyricsRoutes.PlainExtension : LyricsRoutes.LyricsfileExtension;
        return File(Encoding.UTF8.GetBytes(text), LyricsRoutes.TextMediaType, recordingIdentifier + extension);
    }
}

[ApiController]
[Authorize(Roles = LyricsRoutes.Administrator)]
[Route(LyricsRoutes.Queue)]
public sealed class LyricsQueueController(StreamingDbContext database, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery, Range(LyricsProtocol.FirstPage, LyricsProtocol.MaximumPage)] int page = LyricsProtocol.FirstPage,
        [FromQuery, Range(LyricsProtocol.FirstPage, LyricsProtocol.MaximumPageSize)] int pageSize = LyricsProtocol.DefaultPageSize,
        [FromQuery] LyricsDownloadState? state = null,
        [FromQuery, StringLength(LyricsProtocol.MaximumSearchLength)] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (state is not null && !Enum.IsDefined(state.Value)) return BadRequest(LyricsRoutes.UnsupportedState);
        var totalRecordings = await database.Recordings.CountAsync(cancellationToken);
        var totalJobs = await database.RecordingLyrics.CountAsync(cancellationToken);
        var counts = await database.RecordingLyrics.GroupBy(lyrics => lyrics.State)
            .Select(group => new { State = group.Key.ToString(), Count = group.Count() }).ToArrayAsync(cancellationToken);
        var query = from lyrics in database.RecordingLyrics.AsNoTracking()
            join recording in database.Recordings.AsNoTracking() on lyrics.RecordingIdentifier equals recording.Id
            select new { Lyrics = lyrics, Recording = recording };
        if (state is not null) query = query.Where(entry => entry.Lyrics.State == state.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.Trim().ToLowerInvariant();
            query = query.Where(entry => entry.Recording.Title.ToLower().Contains(normalizedSearch)
                || entry.Recording.Release.Artist.Name.ToLower().Contains(normalizedSearch));
        }
        var filteredJobs = await query.CountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var jobs = await query.OrderBy(entry => entry.Lyrics.NextAttemptAt).ThenBy(entry => entry.Lyrics.RecordingIdentifier)
            .Skip((page - LyricsProtocol.FirstPage) * pageSize).Take(pageSize)
            .Select(entry => new { entry.Lyrics.RecordingIdentifier, entry.Recording.Title, Artist = entry.Recording.Release.Artist.Name,
                State = entry.Lyrics.State.ToString(), entry.Lyrics.Attempts, entry.Lyrics.NextAttemptAt,
                entry.Lyrics.LastFailure, entry.Lyrics.LeaseUntil, IsDownloading = entry.Lyrics.LeaseUntil > now }).ToArrayAsync(cancellationToken);
        return Ok(new { totalRecordings, totalJobs, filteredJobs, counts, jobs, page, pageSize });
    }
}
