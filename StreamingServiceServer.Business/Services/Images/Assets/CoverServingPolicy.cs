using System.Text.RegularExpressions;
namespace StreamingServiceServer.Business.Services.Images.Assets;
public static partial class CoverServingPolicy
{
    public const string VersionedCacheControl = "public, max-age=31536000, immutable";
    public const string LegacyCacheControl = "public, max-age=60, must-revalidate";
    public static string CacheControl(string filename) => VersionedFilename().IsMatch(filename) ? VersionedCacheControl : LegacyCacheControl;
    [GeneratedRegex("^[a-f0-9]{64}-v[0-9]+-(full|small|thumbnail)\\.jpg$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedFilename();
}
