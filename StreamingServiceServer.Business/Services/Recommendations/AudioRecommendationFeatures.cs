using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.Services.Recommendations;

public sealed class AudioRecommendationFeatures(IServiceScopeFactory scopes, TimeProvider clock) : IAudioRecommendationFeatures
{
    private readonly SemaphoreSlim loading = new(1, 1);
    private CacheState? current;
    private DateTimeOffset fullRefreshAt;
    private DateTimeOffset latestAnalysisAt = DateTimeOffset.UnixEpoch;
    private readonly Dictionary<Guid, AudioRecommendationFeature> retained = new();
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromMinutes(30);
    private sealed record CacheState(AudioRecommendationSnapshot Snapshot, DateTimeOffset RefreshAt);

    public async Task<AudioRecommendationSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref current);
        if (cached is not null && clock.GetUtcNow() < cached.RefreshAt) return cached.Snapshot;
        await loading.WaitAsync(cancellationToken);
        try
        {
            if (current is not null && clock.GetUtcNow() < current.RefreshAt) return current.Snapshot;
            using var scope = scopes.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
            var fullRefresh = current is null || clock.GetUtcNow() >= fullRefreshAt;
            var query = from analysis in database.RecordingAudioAnalyses.AsNoTracking()
                join recording in database.Recordings.AsNoTracking() on analysis.RecordingId equals recording.Id
                select new { Analysis = analysis, ReleaseId = recording.Release.Id, ArtistId = recording.Release.Artist.Id };
            if (!fullRefresh) query = query.Where(row => row.Analysis.AnalyzedAt >= latestAnalysisAt);
            var rows = await query.ToListAsync(cancellationToken);
            if (fullRefresh) retained.Clear();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var analysis = row.Analysis;
                    retained.Remove(analysis.RecordingId);
                    if (analysis.Profile != RecommendationPolicy.SupportedProfile || analysis.FeatureSchemaVersion != RecommendationPolicy.SupportedFeatureSchemaVersion) continue;
                    var vector = JsonSerializer.Deserialize<float[]>(analysis.EmbeddingJson);
                    if (vector is null || vector.Length != RecommendationPolicy.EmbeddingDimensions || vector.Any(value => !float.IsFinite(value))) continue;
                    var normalized = RecommendationRanking.Normalize(vector);
                    if (normalized is null) continue;
                    using var document = JsonDocument.Parse(analysis.FeaturesJson);
                    var descriptors = document.RootElement.GetProperty("moods").EnumerateObject()
                        .ToDictionary(property => property.Name, property => RestoreDescriptor(property.Value.GetDouble(), analysis.EngineVersion));
                    var manifest = JsonSerializer.Deserialize<ModelIdentity[]>(analysis.ModelsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (manifest is null || manifest.Length == 0) continue;
                    var cohort = analysis.Profile + analysis.EngineVersion + analysis.FeatureSchemaVersion
                        + string.Join(string.Empty, manifest.OrderBy(model => model.Name).Select(model => model.Name + model.Sha256));
                    retained[analysis.RecordingId] = new AudioRecommendationFeature(analysis.RecordingId, row.ReleaseId, row.ArtistId, analysis.AudioSha256,
                        cohort, normalized, descriptors, analysis.Tempo ?? 0, analysis.Energy ?? 0);
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
            }
            var existing = (await database.RecordingAudioAnalyses.AsNoTracking().Select(analysis => analysis.RecordingId).ToArrayAsync(cancellationToken)).ToHashSet();
            foreach (var identifier in retained.Keys.Where(identifier => !existing.Contains(identifier)).ToArray()) retained.Remove(identifier);
            if (rows.Count > 0) latestAnalysisAt = rows.Max(row => row.Analysis.AnalyzedAt);
            if (fullRefresh) fullRefreshAt = clock.GetUtcNow() + FullRefreshInterval;
            var generation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(latestAnalysisAt.ToString("O") + string.Join(string.Empty,
                retained.Values.OrderBy(feature => feature.RecordingId).Select(feature => feature.RecordingId.ToString() + feature.ContentHash + feature.Cohort)))));
            var snapshot = new AudioRecommendationSnapshot(generation, retained.Values.ToArray());
            Volatile.Write(ref current, new CacheState(snapshot, clock.GetUtcNow() + RecommendationPolicy.CacheLifetime));
            return snapshot;
        }
        finally { loading.Release(); }
    }

    public static double RestoreDescriptor(double value, string engineVersion)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) return 0;
        return engineVersion == RecommendationPolicy.SupportedEngine && value >= RecommendationPolicy.MinimumStoredDescriptor
            && value <= RecommendationPolicy.MaximumStoredDescriptor ? Math.Clamp(Math.Log(value / (1 - value)), 0, 1) : value;
    }

    private sealed record ModelIdentity(string Name, string Sha256);
}
