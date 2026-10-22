using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.SpotifyCatalog;

public sealed class RecordingCatalogQuery(StreamingDbContext database) : IRecordingCatalogQuery
{
    public async Task<RecordingCatalogEntry?> Lookup(Guid identifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identifier == Guid.Empty) throw new ArgumentException(RecordingCatalogPolicy.InvalidSearch, nameof(identifier));
        var record = await Project(database.Recordings.AsNoTracking().Where(recording => recording.Id == identifier))
            .SingleOrDefaultAsync(cancellationToken);
        return record is null ? null : Map(record);
    }

    public async Task<IReadOnlyList<RecordingCatalogEntry>> Search(string title, string author, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSearch(title, author, limit);
        var records = await Project(Candidates(database.Recordings.AsNoTracking(), title, author, limit))
            .ToListAsync(cancellationToken);
        return records.Select(Map).ToArray();
    }

    public static IQueryable<Recording> Candidates(IQueryable<Recording> recordings, string title, string author, int limit)
    {
        ValidateSearch(title, author, limit);
        var normalizedTitle = title.Trim().ToLowerInvariant();
        var normalizedAuthor = author.Trim().ToLowerInvariant();
        var authorTerms = normalizedAuthor.Split(RecordingCatalogPolicy.ArtistDelimiters,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        return recordings.Where(recording => recording.Title.ToLower().Contains(normalizedTitle)
                || EF.Functions.TrigramsSimilarity(recording.Title.ToLower(), normalizedTitle) >= RecordingCatalogPolicy.MinimumTitleSimilarity)
            .Where(recording => recording.ArtistCredit.Any(credit =>
                    credit.Name != null && credit.Name.Trim() != RecordingCatalogPolicy.EmptyText
                        ? authorTerms.Any(authorTerm => credit.Name.ToLower().Contains(authorTerm))
                        : credit.Artist != null && authorTerms.Any(authorTerm => credit.Artist.Name.ToLower().Contains(authorTerm)))
                || recording.Release != null && recording.Release.Artist != null
                    && authorTerms.Any(authorTerm => recording.Release.Artist.Name.ToLower().Contains(authorTerm)))
            .OrderByDescending(recording => recording.Title.ToLower() == normalizedTitle)
            .ThenByDescending(recording => EF.Functions.TrigramsSimilarity(recording.Title.ToLower(), normalizedTitle))
            .ThenBy(recording => recording.Id)
            .Take(limit);
    }

    public static IQueryable<RecordingCatalogProjection> Project(IQueryable<Recording> recordings)
    {
        return recordings.Select(recording => new RecordingCatalogProjection
        {
            Identifier = recording.Id,
            Title = recording.Title,
            DurationMilliseconds = recording.Length,
            ArtistNames = recording.ArtistCredit.OrderBy(credit => credit.Id)
                .Select(credit => credit.Name != null && credit.Name.Trim() != RecordingCatalogPolicy.EmptyText
                    ? credit.Name : credit.Artist != null ? credit.Artist.Name : RecordingCatalogPolicy.EmptyText)
                .Where(name => name.Trim() != RecordingCatalogPolicy.EmptyText).ToList(),
            ReleaseArtistName = recording.Release != null && recording.Release.Artist != null
                ? recording.Release.Artist.Name : null
        });
    }

    public static RecordingCatalogEntry Map(RecordingCatalogProjection recording)
    {
        var artistNames = recording.ArtistNames.Select(name => name.Trim())
            .Where(name => name != RecordingCatalogPolicy.EmptyText).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var artistCreditSource = RecordingCatalogPolicy.RecordingCreditSource;
        if (!artistNames.Any() && !string.IsNullOrWhiteSpace(recording.ReleaseArtistName))
        {
            artistNames = [recording.ReleaseArtistName.Trim()];
            artistCreditSource = RecordingCatalogPolicy.ReleaseCreditSource;
        }
        var durationMilliseconds = recording.DurationMilliseconds >= RecordingCatalogPolicy.MinimumDurationMilliseconds
            ? recording.DurationMilliseconds : null;
        return new RecordingCatalogEntry(recording.Identifier.ToString(RecordingCatalogPolicy.IdentifierFormat), recording.Title,
            string.Join(RecordingCatalogPolicy.ArtistSeparator, artistNames), Array.AsReadOnly(artistNames), durationMilliseconds, artistCreditSource);
    }

    private static void ValidateSearch(string title, string author, int limit)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author)
            || title.Length > RecordingCatalogPolicy.MaximumSearchCharacters || author.Length > RecordingCatalogPolicy.MaximumSearchCharacters
            || limit < RecordingCatalogPolicy.MinimumLimit || limit > RecordingCatalogPolicy.MaximumLimit)
            throw new ArgumentException(RecordingCatalogPolicy.InvalidSearch);
    }
}

public sealed class RecordingCatalogProjection
{
    public Guid Identifier { get; init; }
    public string Title { get; init; } = RecordingCatalogPolicy.EmptyText;
    public int? DurationMilliseconds { get; init; }
    public List<string> ArtistNames { get; init; } = [];
    public string? ReleaseArtistName { get; init; }
}
