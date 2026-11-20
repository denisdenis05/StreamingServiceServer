using static StreamingServiceServer.Business.Lyrics.LyricsTrackMatchProtocol;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LyricsTrackMatchStore(StreamingDbContext database, TimeProvider clock)
{

    public async Task SaveAsync(Guid albumIdentifier, IReadOnlyCollection<Guid> evaluatedRecordingIdentifiers,
        string issuesJson, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var analysis = await database.AlbumAnalysisResults.SingleOrDefaultAsync(analysis => analysis.AlbumId == albumIdentifier, cancellationToken);
        if (analysis is null)
        {
            analysis = new AlbumAnalysisResult { AlbumId = albumIdentifier };
            database.AlbumAnalysisResults.Add(analysis);
        }
        var previous = Parse(analysis.TrackMatchIssuesJson);
        var incoming = Parse(issuesJson);
        if (incoming is null) return;
        if (previous is null)
        {
            var albumRecordingIdentifiers = await database.Recordings.Where(recording => recording.Release.Id == albumIdentifier)
                .Select(recording => recording.Id).ToArrayAsync(cancellationToken);
            if (albumRecordingIdentifiers.Any(identifier => !evaluatedRecordingIdentifiers.Contains(identifier))) return;
            previous = [];
        }
        var merged = previous.Where(entry => !evaluatedRecordingIdentifiers.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var entry in incoming)
            merged[entry.Key] = merged.TryGetValue(entry.Key, out var retained) ? MergeIssues(retained, entry.Value) : entry.Value;
        analysis.MismatchedTracksCount = merged.Count;
        analysis.TrackMatchIssuesJson = JsonSerializer.Serialize(merged.Values);
        analysis.LastMismatchCheckedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await database.RecordingLyrics.Where(lyrics => database.Recordings.Any(recording => recording.Id == lyrics.RecordingIdentifier
                && recording.Release.Id == albumIdentifier))
            .ExecuteUpdateAsync(update => update.SetProperty(lyrics => lyrics.NextAttemptAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static JsonElement MergeIssues(JsonElement retained, JsonElement incoming)
    {
        var combined = retained.GetProperty(IssuesProperty).EnumerateArray().Concat(incoming.GetProperty(IssuesProperty).EnumerateArray())
            .GroupBy(issue => issue.GetRawText()).Select(group => group.First()).ToArray();
        var result = JsonSerializer.SerializeToNode(incoming)!;
        result[IssuesProperty] = JsonSerializer.SerializeToNode(combined);
        return JsonSerializer.SerializeToElement(result);
    }

    private static Dictionary<Guid, JsonElement>? Parse(string? issuesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(issuesJson ?? EmptyIssues);
            var entries = document.RootElement.EnumerateArray().ToArray();
            if (entries.Any(entry => !entry.TryGetProperty(IssuesProperty, out var issues) || issues.ValueKind != JsonValueKind.Array)) return null;
            return entries.ToDictionary(entry => entry.GetProperty(RecordingIdentifierProperty).GetGuid(), entry => entry.Clone());
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return null; }
    }
}
