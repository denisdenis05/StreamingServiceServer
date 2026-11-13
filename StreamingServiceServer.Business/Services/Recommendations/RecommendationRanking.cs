using System.Security.Cryptography;
using System.Numerics;
using System.Text;

namespace StreamingServiceServer.Business.Services.Recommendations;

public sealed class RecommendationRanking
{
    private const double MetadataArtistWeight = 0.5;
    private const double EnergyWeight = 0.2;
    private const double FocusedSimilarityWeight = 0.25;
    private const double FocusedMoodWeight = 0.75;
    private const double MoodWeight = 0.4;
    private const double MaximumJitterValue = uint.MaxValue;
    private const string CalmDescriptor = "chill";
    private const string MellowDescriptor = "Mellow";
    private const string InstrumentalDescriptor = "instrumental";

    public IReadOnlyList<AudioRecommendationFeature> Select(IReadOnlyList<AudioRecommendationFeature> candidates,
        IReadOnlyList<AudioRecommendationFeature> seeds, string mode, Guid randomSeed, int count, IReadOnlyList<AudioRecommendationFeature>? alreadySelected = null)
    {
        var cohort = seeds.FirstOrDefault()?.Cohort ?? candidates.GroupBy(candidate => candidate.Cohort)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        var compatibleSeeds = seeds.Where(seed => seed.Cohort == cohort).ToArray();
        var pool = candidates.Where(candidate => candidate.Cohort == cohort)
            .GroupBy(candidate => candidate.ContentHash).Select(group => group.OrderBy(candidate => candidate.RecordingId).First())
            .Select(candidate => new { Feature = candidate, Score = Score(candidate, compatibleSeeds, mode)
                + Jitter(randomSeed, candidate.RecordingId) * RecommendationPolicy.RandomVariation })
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Feature.RecordingId)
            .Take(RecommendationPolicy.CandidatePool).ToList();
        var selected = new List<AudioRecommendationFeature>();
        var redundancy = new Dictionary<Guid, double>();
        var artists = (alreadySelected ?? []).GroupBy(feature => feature.ArtistId).ToDictionary(group => group.Key, group => group.Count());
        var albums = (alreadySelected ?? []).GroupBy(feature => feature.ReleaseId).ToDictionary(group => group.Key, group => group.Count());
        while (selected.Count < count)
        {
            var best = pool.Where(candidate => mode == RecommendationPolicy.SmartShuffle || (artists.GetValueOrDefault(candidate.Feature.ArtistId) < RecommendationPolicy.MaximumPerArtist
                    && albums.GetValueOrDefault(candidate.Feature.ReleaseId) < RecommendationPolicy.MaximumPerAlbum))
                .OrderByDescending(candidate => candidate.Score - redundancy.GetValueOrDefault(candidate.Feature.RecordingId) * RecommendationPolicy.DiversityPenalty)
                .ThenBy(candidate => candidate.Feature.RecordingId).FirstOrDefault();
            if (best is null) break;
            selected.Add(best.Feature);
            artists[best.Feature.ArtistId] = artists.GetValueOrDefault(best.Feature.ArtistId) + 1;
            albums[best.Feature.ReleaseId] = albums.GetValueOrDefault(best.Feature.ReleaseId) + 1;
            pool.Remove(best);
            foreach (var candidate in pool) redundancy[candidate.Feature.RecordingId] = Math.Max(redundancy.GetValueOrDefault(candidate.Feature.RecordingId),
                Cosine(candidate.Feature.Vector, best.Feature.Vector));
        }
        return selected;
    }

    public static float[]? Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        return !double.IsFinite(norm) || norm <= RecommendationPolicy.MinimumNorm ? null : vector.Select(value => (float)(value / norm)).ToArray();
    }

    public static double Cosine(float[] first, float[] second)
    {
        if (first.Length != second.Length) return 0;
        double similarity = 0;
        var index = 0;
        if (Vector.IsHardwareAccelerated)
            for (; index <= first.Length - Vector<float>.Count; index += Vector<float>.Count)
                similarity += Vector.Dot(new Vector<float>(first, index), new Vector<float>(second, index));
        for (; index < first.Length; index++) similarity += first[index] * second[index];
        return Math.Clamp(similarity, -1, 1);
    }

    public static double Jitter(Guid seed, Guid recordingId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString() + recordingId.ToString()));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) / MaximumJitterValue;
    }

    private static double Score(AudioRecommendationFeature candidate, IReadOnlyList<AudioRecommendationFeature> seeds, string mode)
    {
        var similarity = seeds.Count == 0 ? 0 : seeds.Max(seed => Cosine(candidate.Vector, seed.Vector)
            + (seed.Vector.All(value => value == 0) && seed.ArtistId == candidate.ArtistId ? MetadataArtistWeight : 0));
        var mood = mode switch
        {
            RecommendationPolicy.Calm => Math.Max(candidate.Descriptors.GetValueOrDefault(CalmDescriptor), candidate.Descriptors.GetValueOrDefault(MellowDescriptor)) - candidate.Energy * EnergyWeight,
            RecommendationPolicy.Energy => candidate.Energy,
            RecommendationPolicy.Instrumental => candidate.Descriptors.GetValueOrDefault(InstrumentalDescriptor),
            _ => 0
        };
        return mode is RecommendationPolicy.Calm or RecommendationPolicy.Energy or RecommendationPolicy.Instrumental
            ? similarity * FocusedSimilarityWeight + mood * FocusedMoodWeight : similarity + mood * MoodWeight;
    }
}
