using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed record CoverSourceSet(string Full, string Small, string Thumbnail)
{
    public IReadOnlyList<string> Candidates => new[] { Full, Small, Thumbnail }
        .Where(address => !string.IsNullOrWhiteSpace(address)).Distinct(StringComparer.Ordinal).ToArray();
    public string PrimaryAddress => Candidates.FirstOrDefault() ?? string.Empty;
    public string Revision => JsonSerializer.Serialize(new[] { Full, Small, Thumbnail });
    public static CoverSourceSet Capture(string? full, string? small, string? thumbnail) =>
        new(full ?? string.Empty, small ?? string.Empty, thumbnail ?? string.Empty);
    public bool Matches(CoverAssetJob job) => job.SourceRevision.Length == 0
        ? PrimaryAddress == job.SourceAddress : Revision == job.SourceRevision;

    public static Task<CoverSourceSet?> ReadAsync(StreamingDbContext database, CoverEntityKind kind, Guid identifier,
        CancellationToken cancellationToken) => kind == CoverEntityKind.Release
        ? database.Releases.Where(release => release.Id == identifier)
            .Select(release => new CoverSourceSet(release.Cover ?? string.Empty, release.SmallCover ?? string.Empty,
                release.VerySmallCover ?? string.Empty)).SingleOrDefaultAsync(cancellationToken)
        : database.Recordings.Where(recording => recording.Id == identifier)
            .Select(recording => new CoverSourceSet(recording.Cover ?? string.Empty, recording.SmallCover ?? string.Empty,
                recording.VerySmallCover ?? string.Empty)).SingleOrDefaultAsync(cancellationToken);

    public static CoverSourceSet? ParseRevision(string revision)
    {
        try
        {
            var addresses = JsonSerializer.Deserialize<string[]>(revision);
            return addresses is { Length: 3 } ? Capture(addresses[0], addresses[1], addresses[2]) : null;
        }
        catch (JsonException) { return null; }
    }
    public static CoverAssetSet? ParseManifest(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<CoverAssetSet>(manifest);
            return result is not null && ValidIdentity(result.Identity) && ValidIdentity(result.SourceIdentity)
                && ValidVariant(result.Full) && ValidVariant(result.Small) && ValidVariant(result.Thumbnail) ? result : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool ValidIdentity(string? identity) => identity is { Length: 64 } && identity.All(Uri.IsHexDigit);
    private static bool ValidVariant(CoverAssetVariant? variant) => variant is not null
        && !string.IsNullOrWhiteSpace(variant.Filename) && Path.GetFileName(variant.Filename) == variant.Filename
        && variant.Bytes > 0 && ValidIdentity(variant.Checksum);
}
