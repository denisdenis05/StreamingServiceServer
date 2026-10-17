namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed record CoverAssetVariant(string Filename, long Bytes, string Checksum);
public sealed record CoverAssetSet(string Identity, string SourceIdentity, CoverAssetVariant Full, CoverAssetVariant Small, CoverAssetVariant Thumbnail);
public sealed record CoverRenderOutput(string Path, int MaximumDimension);
public sealed class CoverSourceFailure(string message, bool isPermanent, TimeSpan? retryAfter = null, System.Net.HttpStatusCode? statusCode = null)
    : IOException(message)
{
    public bool IsPermanent { get; } = isPermanent;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
}
public interface ICoverAssetStore
{
    Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken);
    Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken);
    Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken);
    Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken);
    Task<bool> IsPresentAsync(CoverAssetSet assets, CancellationToken cancellationToken) => IsValidAsync(assets, cancellationToken);
    Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken);
    Task PruneUnreferencedAsync(Func<string, CancellationToken, Task<bool>> isRetained, CancellationToken cancellationToken) => Task.CompletedTask;
    string NormalizeLocalAddress(string address);
    string PublicAddress(CoverAssetVariant variant);
}
public interface ICoverImageRenderer
{
    Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken);
    async Task RenderVariantsAsync(string input, IReadOnlyList<CoverRenderOutput> outputs, CancellationToken cancellationToken)
    {
        foreach (var output in outputs) await RenderAsync(input, output.Path, output.MaximumDimension, cancellationToken);
    }
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
    public long MaximumDecodedPixels { get; set; } = 16000000;
    public int MaintenanceBatchSize { get; set; } = 50;
    public TimeSpan IntegrityInterval { get; set; } = TimeSpan.FromHours(24);
    public int VerificationCacheEntries { get; set; } = 4096;
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RepairInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan UnreferencedAssetRetention { get; set; } = TimeSpan.FromDays(365);
    public TimeSpan LeaseLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int ParallelJobs { get; set; } = 3;
    public int BatchSize { get; set; } = 50;
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan BodyIdleTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan TemporaryFileRetention { get; set; } = TimeSpan.FromHours(1);
    public int MaximumPendingImports { get; set; } = 32;
    public int CleanupBatchSize { get; set; } = 100;
    public long MaximumStoredBytes { get; set; } = 10737418240;
    public long MinimumFreeBytes { get; set; } = 268435456;
}
