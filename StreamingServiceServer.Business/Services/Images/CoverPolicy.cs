using StreamingServiceServer.Business.Models.MusicSearch;
namespace StreamingServiceServer.Business.Services.Images;

public static class CoverPolicy
{
    public const string DefaultArchiveRoot = "https://coverartarchive.org/";
    public const string ReleasePath = "release/";
    public const string ReleaseGroupPath = "release-group/";
    public const string FullSuffix = "/front";
    public const string SmallSuffix = "/front-500";
    public const string ThumbnailSuffix = "/front-250";
    public const string Empty = "";
    public static string Select(string? local, string? optimized, string? original) => RepairArchiveAddress(
        !string.IsNullOrWhiteSpace(local) ? local : !string.IsNullOrWhiteSpace(optimized) ? optimized : original ?? Empty);
    public static string LocalAddress(string local, string domain, string imagePath)
    {
        if (!Uri.TryCreate(local, UriKind.Absolute, out var address) || !Uri.TryCreate(domain, UriKind.Absolute, out var origin)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp)) return local;
        return origin.AbsoluteUri.TrimEnd('/') + "/" + imagePath.Trim('/') + "/" + address.Segments.Last();
    }
    public static Uri ArchiveRoot(string? configured)
    {
        var address = new Uri(string.IsNullOrWhiteSpace(configured) ? DefaultArchiveRoot : configured, UriKind.Absolute);
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp) throw new ArgumentException(nameof(configured));
        var path = address.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/" + ReleasePath.TrimEnd('/'), StringComparison.Ordinal)) path = path[..^ReleasePath.TrimEnd('/').Length].TrimEnd('/');
        if (path.EndsWith("/" + ReleaseGroupPath.TrimEnd('/'), StringComparison.Ordinal)) path = path[..^ReleaseGroupPath.TrimEnd('/').Length].TrimEnd('/');
        return new UriBuilder(address) { Path = path + "/", Query = Empty, Fragment = Empty }.Uri;
    }
    public static AlbumCoversDto Build(Uri root, Guid identifier, bool group)
    {
        var prefix = new Uri(root, (group ? ReleaseGroupPath : ReleasePath) + identifier).AbsoluteUri;
        return new AlbumCoversDto { Cover = prefix + FullSuffix, SmallCover = prefix + SmallSuffix, VerySmallCover = prefix + ThumbnailSuffix };
    }
    public static string RepairArchiveAddress(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var address) || !address.Host.Equals(new Uri(DefaultArchiveRoot).Host, StringComparison.OrdinalIgnoreCase)) return source;
        var path = address.AbsolutePath;
        var duplicatedRelease = "/release/release/";
        var duplicatedGroup = "/release/release-group/";
        if (path.StartsWith(duplicatedRelease, StringComparison.Ordinal)) path = "/release/" + path[duplicatedRelease.Length..];
        else if (path.StartsWith(duplicatedGroup, StringComparison.Ordinal)) path = "/release-group/" + path[duplicatedGroup.Length..];
        return new UriBuilder(address) { Path = path }.Uri.AbsoluteUri;
    }
}
