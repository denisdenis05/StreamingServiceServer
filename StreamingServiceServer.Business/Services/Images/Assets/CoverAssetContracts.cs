namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed record CoverAssetVariant(string Filename, long Bytes, string Checksum);
public sealed record CoverAssetSet(string Identity, string SourceIdentity, CoverAssetVariant Full, CoverAssetVariant Small, CoverAssetVariant Thumbnail);
public interface ICoverAssetStore
{
    Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken);
    Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken);
    Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken);
    Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken);
    Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken);
    string NormalizeLocalAddress(string address);
    string PublicAddress(CoverAssetVariant variant);
}
public interface ICoverImageRenderer
{
    Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken);
}
public sealed class CoverAssetOptions
{
    public const string Section = "Cover";
    public string Path { get; set; } = "/data/covers";
    public string DomainExtension { get; set; } = "/images/";
    public string Domain { get; set; } = string.Empty;
    public string RendererExecutable { get; set; } = "ffmpeg";
    public long MaximumInputBytes { get; set; } = 20971520;
    public long MaximumOutputBytes { get; set; } = 20971520;
    public int FullDimension { get; set; } = 1400;
    public int SmallDimension { get; set; } = 500;
    public int ThumbnailDimension { get; set; } = 250;
    public TimeSpan SourceLifetime { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RepairInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan UnreferencedAssetRetention { get; set; } = TimeSpan.FromDays(365);
    public TimeSpan LeaseLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int ParallelJobs { get; set; } = 3;
    public int BatchSize { get; set; } = 50;
}
