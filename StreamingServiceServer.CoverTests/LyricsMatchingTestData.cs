namespace StreamingServiceServer.CoverTests;

internal static class LyricsMatchingTestData
{
    public const string EmptySearch = "[]";
    public const string UnknownArtist = "Unknown Artist";
    public const string VariousArtists = "Various Artists";
    public const string Collaboration = "Another Artist & The Example Artist";
    public const string ApostropheTitle = "Girl’s Best Friend";
    public const string StraightApostropheTitle = "Girl's Best Friend";
    public const string LiveTitle = "A Song & Its Echo (Live)";
    public const string LiveAlbum = "The Example Album (Live)";
    public const string RemixAlbum = "The Example Album (Remix)";
    public const string ConflictingPlain = "Different invented words";
    public const string ConflictingSynced = "[00:09.00]First invented line\n[00:15.50]Second invented line";
    public const string MalformedSearch = "[null]";
    public const string SearchRoute = "/api/search";
}
