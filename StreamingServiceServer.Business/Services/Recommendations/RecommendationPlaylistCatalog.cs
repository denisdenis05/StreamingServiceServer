namespace StreamingServiceServer.Business.Services.Recommendations;

public static class RecommendationPlaylistCatalog
{
    public static IReadOnlyList<RecommendationCard> Cards { get; } = Array.AsReadOnly<RecommendationCard>([
        new(RecommendationPolicy.Daily, "Today's playlist", "A daily selection shaped by your likes and listening."),
        new(RecommendationPolicy.SmartShuffle, "Smart shuffle", "A random starting point with a varied musical flow."),
        new(RecommendationPolicy.Calm, "Take it easy", "Calmer sounds from your library."),
        new(RecommendationPolicy.Energy, "Pick up the pace", "A more energetic selection."),
        new(RecommendationPolicy.Instrumental, "Mostly instrumental", "Tracks with stronger instrumental descriptors.")
    ]);
}
