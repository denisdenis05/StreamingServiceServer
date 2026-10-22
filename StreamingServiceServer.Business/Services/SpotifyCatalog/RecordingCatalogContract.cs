namespace StreamingServiceServer.Business.Services.SpotifyCatalog;

public sealed record RecordingCatalogEntry(string Identifier, string Title, string Author,
    IReadOnlyList<string> ArtistNames, int? DurationMilliseconds, string ArtistCreditSource);

public interface IRecordingCatalogQuery
{
    Task<RecordingCatalogEntry?> Lookup(Guid identifier, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingCatalogEntry>> Search(string title, string author, int limit, CancellationToken cancellationToken);
}

public static class RecordingCatalogPolicy
{
    public const int MinimumLimit = 1;
    public const int MaximumLimit = 20;
    public const int DefaultLimit = 10;
    public const int MaximumSearchCharacters = 300;
    public const int MinimumDurationMilliseconds = 0;
    public const double MinimumTitleSimilarity = 0.25;
    public const string EmptyText = "";
    public const string ArtistSeparator = ", ";
    public const string IdentifierFormat = "D";
    public const string InvalidSearch = "A title, artist, and bounded result limit are required.";
    public const string RecordingCreditSource = "recording";
    public const string ReleaseCreditSource = "release";
    public static readonly string[] ArtistDelimiters = [",", " & ", " feat. ", " feat "];
}
