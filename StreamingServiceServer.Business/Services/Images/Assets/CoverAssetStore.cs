using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class CoverAssetStore(Func<HttpClient> createClient, ICoverImageRenderer renderer,
    IOptions<CoverAssetOptions> configuration, TimeProvider clock) : ICoverAssetStore
{
    private const string AssetVersion = "-v1-";
    private const string ImageExtension = ".jpg";
    private const string ManifestExtension = ".asset.json";
    private const string SourceExtension = ".source.json";
    private const string OriginalExtension = ".original";
    private const string TemporaryExtension = ".partial";
    private const string FailureMessage = "The cover asset is missing, incomplete or unsupported.";
    private const int CopyBufferBytes = 65536;
    private const int SignatureBytes = 12;
    private readonly ConcurrentDictionary<string, Lazy<Task<CoverAssetSet>>> pending = new();
    private readonly SemaphoreSlim rendering = new(configuration.Value.ParallelJobs);
    private sealed record SourceManifest(string Identity, long Bytes, DateTimeOffset DownloadedAt);
    private CoverAssetOptions Options => configuration.Value;
    public string NormalizeLocalAddress(string address) => CoverPolicy.LocalAddress(address, Options.Domain, Options.DomainExtension);
    public string PublicAddress(CoverAssetVariant variant) => Options.Domain.TrimEnd('/') + "/" + Options.DomainExtension.Trim('/') + "/" + variant.Filename;
    public async Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken)
    {
        var source = CoverPolicy.RepairArchiveAddress(sourceAddress);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)) throw new IOException(FailureMessage);
        var local = LocalOriginal(address);
        if (local is not null)
        {
            var filename = System.IO.Path.GetFileName(local);
            var versionPosition = filename.IndexOf(AssetVersion, StringComparison.Ordinal);
            if (versionPosition > 0)
            {
                var originalIdentity = filename[..versionPosition];
                var manifest = await ReadSetAsync(AssetPath(originalIdentity + ManifestExtension), cancellationToken);
                if (manifest is not null)
                {
                    if (await IsValidAsync(manifest, cancellationToken)) return manifest;
                    try { return await RestoreAsync(manifest, cancellationToken); } catch (IOException) {}
                }
            }
        }
        var identity = Hash(Encoding.UTF8.GetBytes(source));
        var operation = pending.GetOrAdd(identity, _ => new Lazy<Task<CoverAssetSet>>(() => DownloadAsync(address, identity, cancellationToken)));
        var completion = operation.Value;
        _ = completion.ContinueWith(completed =>
        {
            pending.TryRemove(new KeyValuePair<string, Lazy<Task<CoverAssetSet>>>(identity, operation));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await completion.WaitAsync(cancellationToken);
    }
    private async Task<CoverAssetSet> DownloadAsync(Uri address, string sourceIdentity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Options.Path);
        var mappingPath = AssetPath(sourceIdentity + SourceExtension);
        try
        {
            var mapping = JsonSerializer.Deserialize<SourceManifest>(await File.ReadAllTextAsync(mappingPath, cancellationToken));
            if (mapping is not null && !string.IsNullOrEmpty(mapping.Identity) && mapping.Identity.Length == 64
                && mapping.Identity.All(Uri.IsHexDigit) && clock.GetUtcNow() - mapping.DownloadedAt < Options.SourceLifetime)
            {
                var original = AssetPath(mapping.Identity + OriginalExtension);
                if (await ValidOriginalAsync(original, mapping.Identity, mapping.Bytes, cancellationToken)) return await RenderAsync(original, mapping.Identity, cancellationToken);
            }
        }
        catch (Exception failure) when (failure is IOException or JsonException) {}
        var local = LocalOriginal(address);
        CoverAssetSet result;
        if (local is not null && File.Exists(local))
        {
            await using var input = File.OpenRead(local);
            result = await ImportAsync(input, cancellationToken);
        }
        else
        {
            using var client = createClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > Options.MaximumInputBytes) throw new IOException(FailureMessage);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && contentType != "application/octet-stream") throw new IOException(FailureMessage);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await ImportAsync(input, cancellationToken);
            if (response.Content.Headers.ContentLength is long expectedLength
                && new FileInfo(AssetPath(result.SourceIdentity + OriginalExtension)).Length != expectedLength) throw new IOException(FailureMessage);
        }
        var originalInformation = new FileInfo(AssetPath(result.SourceIdentity + OriginalExtension));
        await WriteJsonAsync(mappingPath, new SourceManifest(result.SourceIdentity, originalInformation.Length, clock.GetUtcNow()), cancellationToken);
        return result;
    }
    public async Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Options.Path);
        var temporary = AssetPath(Guid.NewGuid().ToString("N") + TemporaryExtension);
        try
        {
            long length = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                var buffer = new byte[CopyBufferBytes];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += count;
                    if (length > Options.MaximumInputBytes) throw new IOException(FailureMessage);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            await ValidateSignatureAsync(temporary, cancellationToken);
            var identity = await ChecksumAsync(temporary, cancellationToken);
            var original = AssetPath(identity + OriginalExtension);
            if (!await ValidOriginalAsync(original, identity, length, cancellationToken)) File.Move(temporary, original, true);
            return await RenderAsync(original, identity, cancellationToken);
        }
        finally { File.Delete(temporary); }
    }
    private async Task<CoverAssetSet> RenderAsync(string original, string sourceIdentity, CancellationToken cancellationToken)
    {
        var identity = RenderIdentity(sourceIdentity);
        var manifestPath = AssetPath(identity + ManifestExtension);
        var existing = await ReadSetAsync(manifestPath, cancellationToken);
        if (existing is not null && await IsValidAsync(existing, cancellationToken)) return existing;
        await rendering.WaitAsync(cancellationToken);
        try
        {
            existing = await ReadSetAsync(manifestPath, cancellationToken);
            if (existing is not null && await IsValidAsync(existing, cancellationToken)) return existing;
            var full = await VariantAsync(original, identity, "full", Options.FullDimension, cancellationToken);
            var small = await VariantAsync(original, identity, "small", Options.SmallDimension, cancellationToken);
            var thumbnail = await VariantAsync(original, identity, "thumbnail", Options.ThumbnailDimension, cancellationToken);
            var assets = new CoverAssetSet(identity, sourceIdentity, full, small, thumbnail);
            await WriteJsonAsync(manifestPath, assets, cancellationToken);
            return assets;
        }
        finally { rendering.Release(); }
    }
    private async Task<CoverAssetVariant> VariantAsync(string original, string identity, string variant, int dimension, CancellationToken cancellationToken)
    {
        var filename = identity + AssetVersion + variant + ImageExtension;
        var temporary = AssetPath(filename + Guid.NewGuid().ToString("N") + TemporaryExtension);
        try
        {
            await renderer.RenderAsync(original, temporary, dimension, cancellationToken);
            var information = new FileInfo(temporary);
            if (!information.Exists || information.Length <= 0 || information.Length > Options.MaximumOutputBytes) throw new IOException(FailureMessage);
            var result = new CoverAssetVariant(filename, information.Length, await ChecksumAsync(temporary, cancellationToken));
            File.Move(temporary, AssetPath(filename), true);
            return result;
        }
        finally { File.Delete(temporary); }
    }
    public async Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(assets.SourceIdentity) || assets.SourceIdentity.Length != 64 || !assets.SourceIdentity.All(Uri.IsHexDigit)) throw new IOException(FailureMessage);
        var original = AssetPath(assets.SourceIdentity + OriginalExtension);
        var information = new FileInfo(original);
        if (!information.Exists || !await ValidOriginalAsync(original, assets.SourceIdentity, information.Length, cancellationToken)) throw new IOException(FailureMessage);
        return await RenderAsync(original, assets.SourceIdentity, cancellationToken);
    }
    public async Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken)
    {
        if (assets.Identity != RenderIdentity(assets.SourceIdentity)) return false;
        foreach (var variant in new[] { assets.Full, assets.Small, assets.Thumbnail })
        {
            if (System.IO.Path.GetFileName(variant.Filename) != variant.Filename) return false;
            var path = AssetPath(variant.Filename);
            if (!await ValidOriginalAsync(path, variant.Checksum, variant.Bytes, cancellationToken)) return false;
        }
        return true;
    }
    private async Task<bool> ValidOriginalAsync(string path, string checksum, long bytes, CancellationToken cancellationToken)
    {
        try { var information = new FileInfo(path); return information.Exists && information.Length > 0 && information.Length == bytes
            && await ChecksumAsync(path, cancellationToken) == checksum; } catch (IOException) { return false; }
    }
    private static async Task ValidateSignatureAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path); var signature = new byte[SignatureBytes];
        var count = await input.ReadAsync(signature, cancellationToken);
        var jpeg = count >= 3 && signature[0] == 0xff && signature[1] == 0xd8 && signature[2] == 0xff;
        var png = count >= 8 && signature.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var webp = count >= SignatureBytes && Encoding.ASCII.GetString(signature, 0, 4) == "RIFF" && Encoding.ASCII.GetString(signature, 8, 4) == "WEBP";
        if (!jpeg && !png && !webp) throw new IOException(FailureMessage);
    }
    public async Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Options.Path)) return;
        var threshold = clock.GetUtcNow() - Options.UnreferencedAssetRetention;
        foreach (var path in Directory.EnumerateFiles(Options.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = System.IO.Path.GetFileName(path);
            var information = new FileInfo(path);
            if (information.LastWriteTimeUtc >= threshold.UtcDateTime) continue;
            var identityLength = 64;
            var owned = filename.Length >= identityLength && filename[..identityLength].All(Uri.IsHexDigit)
                && (filename.EndsWith(ManifestExtension, StringComparison.Ordinal) || filename.EndsWith(SourceExtension, StringComparison.Ordinal)
                    || filename.EndsWith(OriginalExtension, StringComparison.Ordinal) || filename.Contains(AssetVersion, StringComparison.Ordinal));
            if (!owned) continue;
            if (retainedIdentities.Contains(filename[..identityLength])) continue;
            if (filename.EndsWith(SourceExtension, StringComparison.Ordinal))
            {
                try
                {
                    var mapping = JsonSerializer.Deserialize<SourceManifest>(await File.ReadAllTextAsync(path, cancellationToken));
                    if (mapping is not null && retainedIdentities.Contains(mapping.Identity)) continue;
                }
                catch (Exception failure) when (failure is IOException or JsonException) {}
            }
            try { File.Delete(path); } catch (IOException) {}
        }
    }
    private string? LocalOriginal(Uri address)
    {
        if (!Uri.TryCreate(Options.Domain, UriKind.Absolute, out var origin) || address.Scheme != origin.Scheme || address.Authority != origin.Authority
            || !address.AbsolutePath.StartsWith(origin.AbsolutePath.TrimEnd('/') + "/" + Options.DomainExtension.Trim('/') + "/", StringComparison.Ordinal)) return null;
        var filename = Uri.UnescapeDataString(address.Segments.Last());
        return System.IO.Path.GetFileName(filename) == filename ? AssetPath(filename) : null;
    }
    private string RenderIdentity(string sourceIdentity) => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
        sourceIdentity, AssetVersion, Options.FullDimension, Options.SmallDimension, Options.ThumbnailDimension })));
    private string AssetPath(string filename) => System.IO.Path.Combine(Options.Path, filename);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant(); }
    private static async Task<CoverAssetSet?> ReadSetAsync(string path, CancellationToken cancellationToken)
    { try { return JsonSerializer.Deserialize<CoverAssetSet>(await File.ReadAllTextAsync(path, cancellationToken)); } catch (Exception failure) when (failure is IOException or JsonException) { return null; } }
    private static async Task WriteJsonAsync<Value>(string path, Value value, CancellationToken cancellationToken)
    {
        var temporary = path + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
}
