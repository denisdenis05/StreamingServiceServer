using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Services.Recommendations;

public interface IRecommendationPreviewCache
{
    Task<RecommendationPlaylistPreview> GetAsync(Guid userId, RecommendationRequest request,
        Func<CancellationToken, Task<RecommendationPlaylistPreview>> generate, bool refresh, CancellationToken cancellationToken);
    Task InvalidateDailyAsync(Guid userId, CancellationToken cancellationToken);
    Task DeleteExpiredAsync(CancellationToken cancellationToken);
}

public sealed class RecommendationPreviewCache(StreamingDbContext database, TimeProvider clock,
    ILibraryService library, IRecommendationCursorCodec cursors) : IRecommendationPreviewCache
{
    private const int CachePolicyVersion = 1;

    public async Task<RecommendationPlaylistPreview> GetAsync(Guid userId, RecommendationRequest request,
        Func<CancellationToken, Task<RecommendationPlaylistPreview>> generate, bool refresh, CancellationToken cancellationToken)
    {
        var normalized = request with { Exclude = request.Exclude?.Distinct().Order().ToArray() };
        var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { CachePolicyVersion, Request = normalized })));
        if (!refresh && await ReadAsync(userId, key, cancellationToken) is { } cached)
            return await WithCurrentStateAsync(userId, cached, cancellationToken);
        return await LockedAsync(userId, async () =>
        {
            if (!refresh && await ReadAsync(userId, key, cancellationToken) is { } shared)
                return await WithCurrentStateAsync(userId, shared, cancellationToken);
            var started = clock.GetUtcNow();
            var expires = new DateTimeOffset(started.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            var generated = await generate(cancellationToken);
            if (clock.GetUtcNow() < expires)
            {
                var payload = JsonSerializer.Serialize(generated);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "RecommendationPlaylistCaches" ("UserId", "CacheKey", "Mode", "Payload", "ExpiresAt")
                    VALUES ({userId}, {key}, {request.Mode}, CAST({payload} AS jsonb), {expires})
                    ON CONFLICT ("UserId", "CacheKey") DO UPDATE
                    SET "Payload" = EXCLUDED."Payload", "ExpiresAt" = EXCLUDED."ExpiresAt", "Mode" = EXCLUDED."Mode"
                    """, cancellationToken);
            }
            return await WithCurrentStateAsync(userId, generated, cancellationToken);
        }, cancellationToken);
    }

    private async Task<RecommendationPlaylistPreview?> ReadAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var payload = await database.RecommendationPlaylistCaches.AsNoTracking()
            .Where(cache => cache.UserId == userId && cache.CacheKey == key && cache.ExpiresAt > now)
            .Select(cache => cache.Payload).SingleOrDefaultAsync(cancellationToken);
        return payload is null ? null : JsonSerializer.Deserialize<RecommendationPlaylistPreview>(payload);
    }

    private async Task<RecommendationPlaylistPreview> WithCurrentStateAsync(Guid userId, RecommendationPlaylistPreview preview, CancellationToken cancellationToken)
    {
        var liked = await library.GetLikedRecordingIdsAsync(userId, preview.Page.Tracks.Select(track => track.Id).ToArray(), cancellationToken);
        foreach (var track in preview.Page.Tracks) track.IsLiked = liked.Contains(track.Id);
        if (preview.Page.ContinuationToken is { } token)
        {
            var session = cursors.Decode(token);
            var renewed = cursors.Encode(session with { ExpiresAt = clock.GetUtcNow() + RecommendationPolicy.SessionLifetime });
            preview = preview with { Page = preview.Page with { ContinuationToken = renewed } };
        }
        if (preview.DailyVersion is { } version)
        {
            var daily = await database.RecommendationDailyMixes.AsNoTracking()
                .Where(mix => mix.UserId == userId && mix.Version == version).SingleOrDefaultAsync(cancellationToken);
            if (daily is not null) preview = preview with { SavedPlaylistId = daily.SavedPlaylistId };
        }
        return preview;
    }

    public async Task InvalidateDailyAsync(Guid userId, CancellationToken cancellationToken) =>
        await LockedAsync(userId, () => database.RecommendationPlaylistCaches
            .Where(cache => cache.UserId == userId && cache.Mode == RecommendationPolicy.Daily).ExecuteDeleteAsync(cancellationToken), cancellationToken);

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await database.RecommendationPlaylistCaches.Where(cache => cache.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<Result> LockedAsync<Result>(Guid userId, Func<Task<Result>> action, CancellationToken cancellationToken)
    {
        var lockKey = BitConverter.ToInt64(userId.ToByteArray());
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({lockKey})", cancellationToken);
            return await action();
        }
        finally
        {
            try { await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({lockKey})", CancellationToken.None); }
            finally { await database.Database.CloseConnectionAsync(); }
        }
    }
}
