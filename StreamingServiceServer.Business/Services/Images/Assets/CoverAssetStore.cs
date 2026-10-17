using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
namespace StreamingServiceServer.Business.Services.Images.Assets;

public sealed class CoverAssetStore(Func<HttpClient> createClient, ICoverImageRenderer renderer,
    IOptions<CoverAssetOptions> configuration, TimeProvider clock, ILogger<CoverAssetStore>? logger = null) : ICoverAssetStore, IDisposable
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
    private readonly SemaphoreSlim imports = new(configuration.Value.ParallelJobs);
    private readonly SemaphoreSlim pruning = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CoverAssetSet>>> pendingRenders = new();
    private readonly ConcurrentDictionary<string, byte> activeFiles = new();
    private readonly CoverStorageAdmission storage = new(configuration.Value);
    private IEnumerator<string>? cleanupFiles;
    private int pendingImports;
    private readonly ConcurrentDictionary<string, DateTimeOffset> publicationProtection = new();
    private sealed record SourceManifest(string Identity, long Bytes, DateTimeOffset DownloadedAt, string? EntityTag = null, DateTimeOffset? LastModified = null);
    private CoverAssetOptions Options => configuration.Value;
    public string NormalizeLocalAddress(string address) => CoverPolicy.LocalAddress(address, Options.Domain, Options.DomainExtension);
    public string PublicAddress(CoverAssetVariant variant) => Options.Domain.TrimEnd('/') + "/" + Options.DomainExtension.Trim('/') + "/" + variant.Filename;
    public async Task<CoverAssetSet> GetOrCreateAsync(string sourceAddress, CancellationToken cancellationToken)
    {
        var source = CoverPolicy.RepairArchiveAddress(sourceAddress);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)) throw new CoverSourceFailure(FailureMessage, true);
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
                    ProtectIdentity(manifest.SourceIdentity); ProtectIdentity(manifest.Identity);
                    if (await IsValidAsync(manifest, cancellationToken)) return manifest;
                    try { return await RestoreAsync(manifest, cancellationToken); } catch (IOException) {}
                }
            }
        }
        var identity = Hash(Encoding.UTF8.GetBytes(source));
        ProtectIdentity(identity);
        var operation = pending.GetOrAdd(identity, _ => new Lazy<Task<CoverAssetSet>>(() => BoundedAsync(token => DownloadAsync(address, identity, token))));
        var completion = operation.Value;
        _ = completion.ContinueWith(completed =>
        {
            _ = completed.Exception;
            pending.TryRemove(new KeyValuePair<string, Lazy<Task<CoverAssetSet>>>(identity, operation));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await completion.WaitAsync(cancellationToken);
    }
    private async Task<CoverAssetSet> DownloadAsync(Uri address, string sourceIdentity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Options.Path);
        var mappingPath = AssetPath(sourceIdentity + SourceExtension);
        SourceManifest? mapping = null;
        try
        {
            mapping = JsonSerializer.Deserialize<SourceManifest>(await File.ReadAllTextAsync(mappingPath, cancellationToken));
            if (mapping is not null && !string.IsNullOrEmpty(mapping.Identity) && mapping.Identity.Length == 64
                && mapping.Identity.All(Uri.IsHexDigit) && clock.GetUtcNow() - mapping.DownloadedAt < Options.SourceLifetime)
            {
                ProtectIdentity(mapping.Identity); ProtectIdentity(RenderIdentity(mapping.Identity));
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
            if (mapping is not null && (!ValidIdentity(mapping.Identity)
                || !await ValidOriginalAsync(AssetPath(mapping.Identity + OriginalExtension), mapping.Identity, mapping.Bytes, cancellationToken))) mapping = null;
            if (mapping?.EntityTag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", mapping.EntityTag);
            if (mapping?.LastModified is not null) request.Headers.IfModifiedSince = mapping.LastModified;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && mapping is not null)
            {
                var original = AssetPath(mapping.Identity + OriginalExtension);
                if (ValidIdentity(mapping.Identity) && await ValidOriginalAsync(original, mapping.Identity, mapping.Bytes, cancellationToken))
                {
                    await WriteJsonAsync(mappingPath, mapping with { DownloadedAt = clock.GetUtcNow() }, cancellationToken);
                    return await RenderAsync(original, mapping.Identity, cancellationToken);
                }
                throw new CoverSourceFailure(FailureMessage, false);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
                var permanent = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest
                    or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                throw new CoverSourceFailure(FailureMessage, permanent, retryAfter, response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > Options.MaximumInputBytes) throw new CoverSourceFailure(FailureMessage, true);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && contentType != "application/octet-stream") throw new CoverSourceFailure(FailureMessage, true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await ImportBoundedAsync(input, response.Content.Headers.ContentLength, cancellationToken);
            mapping = new SourceManifest(result.SourceIdentity, new FileInfo(AssetPath(result.SourceIdentity + OriginalExtension)).Length,
                clock.GetUtcNow(), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }
        var originalInformation = new FileInfo(AssetPath(result.SourceIdentity + OriginalExtension));
        await WriteJsonAsync(mappingPath, new SourceManifest(result.SourceIdentity, originalInformation.Length, clock.GetUtcNow(), mapping?.EntityTag, mapping?.LastModified), cancellationToken);
        return result;
    }
    public Task<CoverAssetSet> ImportAsync(Stream input, CancellationToken cancellationToken) =>
        ImportBoundedAsync(input, null, cancellationToken);
    private async Task<CoverAssetSet> ImportBoundedAsync(Stream input, long? expectedBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(Options.OperationTimeout);
        if (Interlocked.Increment(ref pendingImports) > Options.MaximumPendingImports)
        {
            Interlocked.Decrement(ref pendingImports);
            throw new CoverSourceFailure("The cover import queue is full.", false, statusCode: HttpStatusCode.ServiceUnavailable);
        }
        try
        {
            await imports.WaitAsync(timeout.Token);
            try
            {
                using var reservation = await storage.ReserveAsync(timeout.Token);
                return await ImportCoreAsync(input, expectedBytes, timeout.Token);
            }
            finally { imports.Release(); }
        }
        finally { Interlocked.Decrement(ref pendingImports); }
    }
    private async Task<CoverAssetSet> ImportCoreAsync(Stream input, long? expectedBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Options.Path);
        var temporary = AssetPath(Guid.NewGuid().ToString("N") + TemporaryExtension);
        activeFiles.TryAdd(temporary, 0);
        try
        {
            long length = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                var buffer = new byte[CopyBufferBytes];
                int count;
                while ((count = await ReadBodyAsync(input, buffer, cancellationToken)) > 0)
                {
                    length += count;
                    if (length > Options.MaximumInputBytes) throw new CoverSourceFailure(FailureMessage, true);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (expectedBytes is not null && length != expectedBytes) throw new CoverSourceFailure(FailureMessage, false);
            await ValidateSignatureAsync(temporary, cancellationToken);
            var identity = await ChecksumAsync(temporary, cancellationToken);
            var original = AssetPath(identity + OriginalExtension);
            return await RenderAsync(temporary, identity, cancellationToken);
        }
        finally { CleanupTemporary(temporary); }
    }
    private async Task<CoverAssetSet> RenderAsync(string original, string sourceIdentity, CancellationToken cancellationToken)
    {
        ProtectIdentity(sourceIdentity); ProtectIdentity(RenderIdentity(sourceIdentity));
        var existing = await ReadSetAsync(AssetPath(RenderIdentity(sourceIdentity) + ManifestExtension), cancellationToken);
        if (existing is not null && await IsValidAsync(existing, cancellationToken)) return existing;
        var operation = pendingRenders.GetOrAdd(RenderIdentity(sourceIdentity), _ =>
            new Lazy<Task<CoverAssetSet>>(() => StartRenderAsync(original, sourceIdentity)));
        Task<CoverAssetSet> completion;
        try { completion = operation.Value; }
        catch { pendingRenders.TryRemove(new KeyValuePair<string, Lazy<Task<CoverAssetSet>>>(RenderIdentity(sourceIdentity), operation)); throw; }
        _ = completion.ContinueWith(completed => {
            _ = completed.Exception;
            pendingRenders.TryRemove(new KeyValuePair<string, Lazy<Task<CoverAssetSet>>>(RenderIdentity(sourceIdentity), operation));
        },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await completion.WaitAsync(cancellationToken);
    }
    private Task<CoverAssetSet> StartRenderAsync(string original, string sourceIdentity)
    {
        var snapshot = AssetPath(Guid.NewGuid().ToString("N") + TemporaryExtension);
        activeFiles.TryAdd(snapshot, 0);
        try { File.Copy(original, snapshot, true); }
        catch { CleanupTemporary(snapshot); throw; }
        return BoundedAsync(async token =>
        {
            try { return await RenderCoreAsync(snapshot, sourceIdentity, token); }
            finally { CleanupTemporary(snapshot); }
        });
    }
    private async Task<CoverAssetSet> RenderCoreAsync(string original, string sourceIdentity, CancellationToken cancellationToken)
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
            using var reservation = await storage.ReserveAsync(cancellationToken);
            var specifications = new[] { (Name: "full", Dimension: Options.FullDimension),
                (Name: "small", Dimension: Options.SmallDimension), (Name: "thumbnail", Dimension: Options.ThumbnailDimension) };
            var outputs = specifications.Select(specification => new CoverRenderOutput(AssetPath(identity + AssetVersion
                + specification.Name + ImageExtension + Guid.NewGuid().ToString("N") + TemporaryExtension), specification.Dimension)).ToArray();
            foreach (var output in outputs) activeFiles.TryAdd(output.Path, 0);
            var newlyPublished = new List<string>();
            try
            {
                await renderer.RenderVariantsAsync(original, outputs, cancellationToken);
                var variants = new List<CoverAssetVariant>();
                foreach (var pair in outputs.Zip(specifications))
                {
                    var information = new FileInfo(pair.First.Path);
                    if (!information.Exists || information.Length <= 0 || information.Length > Options.MaximumOutputBytes)
                        throw new CoverSourceFailure(FailureMessage, true);
                    variants.Add(new CoverAssetVariant(identity + AssetVersion + pair.Second.Name + ImageExtension,
                        information.Length, await ChecksumAsync(pair.First.Path, cancellationToken)));
                }
                var retainedOriginal = AssetPath(sourceIdentity + OriginalExtension);
                if (!await ValidOriginalAsync(retainedOriginal, sourceIdentity, new FileInfo(original).Length, cancellationToken))
                {
                    var originalTemporary = retainedOriginal + Guid.NewGuid().ToString("N") + TemporaryExtension;
                    activeFiles.TryAdd(originalTemporary, 0);
                    try {
                        File.Copy(original, originalTemporary, true);
                        if (!File.Exists(retainedOriginal)) newlyPublished.Add(retainedOriginal);
                        MovePublished(originalTemporary, retainedOriginal);
                    }
                    finally { CleanupTemporary(originalTemporary); }
                }
                foreach (var pair in outputs.Zip(variants))
                {
                    var destination = AssetPath(pair.Second.Filename);
                    if (!File.Exists(destination)) newlyPublished.Add(destination);
                    MovePublished(pair.First.Path, destination);
                }
                var assets = new CoverAssetSet(identity, sourceIdentity, variants[0], variants[1], variants[2]);
                await WriteJsonAsync(manifestPath, assets, cancellationToken);
                return assets;
            }
            catch
            {
                foreach (var path in newlyPublished)
                {
                    var information = new FileInfo(path);
                    if (!information.Exists) continue;
                    var removedBytes = information.Length;
                    File.Delete(path); storage.RecordMutation(-removedBytes);
                }
                throw;
            }
            finally
            {
                foreach (var output in outputs) { CleanupTemporary(output.Path); }
            }
        }
        finally { rendering.Release(); }
    }
    public async Task<CoverAssetSet> RestoreAsync(CoverAssetSet assets, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(assets.SourceIdentity) || assets.SourceIdentity.Length != 64 || !assets.SourceIdentity.All(Uri.IsHexDigit)) throw new IOException(FailureMessage);
        ProtectIdentity(assets.SourceIdentity); ProtectIdentity(RenderIdentity(assets.SourceIdentity));
        var original = AssetPath(assets.SourceIdentity + OriginalExtension);
        var information = new FileInfo(original);
        if (!information.Exists || !await ValidOriginalAsync(original, assets.SourceIdentity, information.Length, cancellationToken)) throw new IOException(FailureMessage);
        return await RenderAsync(original, assets.SourceIdentity, cancellationToken);
    }
    public async Task<bool> IsValidAsync(CoverAssetSet assets, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(assets.SourceIdentity) || assets.Identity != RenderIdentity(assets.SourceIdentity)) return false;
        foreach (var variant in new[] { assets.Full, assets.Small, assets.Thumbnail })
        {
            if (variant is null || string.IsNullOrWhiteSpace(variant.Filename) || !ValidIdentity(variant.Checksum)
                || variant.Bytes <= 0 || System.IO.Path.GetFileName(variant.Filename) != variant.Filename) return false;
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
        if (!jpeg && !png && !webp) throw new CoverSourceFailure(FailureMessage, true);
    }
    public async Task<bool> IsPresentAsync(CoverAssetSet assets, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidIdentity(assets.SourceIdentity) || assets.Identity != RenderIdentity(assets.SourceIdentity)) return false;
        foreach (var variant in new[] { assets.Full, assets.Small, assets.Thumbnail })
        {
            if (variant is null || string.IsNullOrWhiteSpace(variant.Filename) || !ValidIdentity(variant.Checksum)
                || variant.Bytes <= 0 || System.IO.Path.GetFileName(variant.Filename) != variant.Filename) return false;
            var information = new FileInfo(AssetPath(variant.Filename));
            if (!information.Exists || information.Length != variant.Bytes || information.Length <= 0) return false;
        }
        return await Task.FromResult(true);
    }
    public async Task PruneAsync(IReadOnlySet<string> retainedIdentities, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Options.Path)) return;
        foreach (var path in Directory.EnumerateFiles(Options.Path))
            await PruneFileAsync(path, (identity, _) => Task.FromResult(retainedIdentities.Contains(identity)), cancellationToken);
    }
    public async Task PruneUnreferencedAsync(Func<string, CancellationToken, Task<bool>> isRetained, CancellationToken cancellationToken)
    {
        await pruning.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(Options.Path)) return;
            cleanupFiles ??= Directory.EnumerateFiles(Options.Path).GetEnumerator();
            for (var processed = 0; processed < Options.CleanupBatchSize; processed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cleanupFiles.MoveNext()) { cleanupFiles.Dispose(); cleanupFiles = null; break; }
                await PruneFileAsync(cleanupFiles.Current, isRetained, cancellationToken);
            }
        }
        catch { cleanupFiles?.Dispose(); cleanupFiles = null; throw; }
        finally { pruning.Release(); }
    }
    private async Task PruneFileAsync(string path, Func<string, CancellationToken, Task<bool>> isRetained, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (activeFiles.ContainsKey(path)) return;
        var filename = System.IO.Path.GetFileName(path);
        var information = new FileInfo(path);
        if (!information.Exists) return;
        if (filename.EndsWith(TemporaryExtension, StringComparison.Ordinal))
        {
            var prefix = filename.Split('.')[0];
            var ownedTemporary = prefix.Length == 32 && prefix.All(Uri.IsHexDigit)
                || filename.Length >= 64 && filename[..64].All(Uri.IsHexDigit);
            if (ownedTemporary && information.LastWriteTimeUtc < (clock.GetUtcNow() - Options.TemporaryFileRetention).UtcDateTime) { File.Delete(path); }
            return;
        }
        if (information.LastWriteTimeUtc >= (clock.GetUtcNow() - Options.UnreferencedAssetRetention).UtcDateTime) return;
        var owned = filename.Length >= 64 && filename[..64].All(Uri.IsHexDigit)
            && (filename.EndsWith(ManifestExtension, StringComparison.Ordinal) || filename.EndsWith(SourceExtension, StringComparison.Ordinal)
                || filename.EndsWith(OriginalExtension, StringComparison.Ordinal) || filename.Contains(AssetVersion, StringComparison.Ordinal));
        if (!owned || IsProtected(filename[..64]) || await isRetained(filename[..64], cancellationToken)) return;
        if (filename.EndsWith(SourceExtension, StringComparison.Ordinal))
        {
            try
            {
                var mapping = JsonSerializer.Deserialize<SourceManifest>(await File.ReadAllTextAsync(path, cancellationToken));
                if (mapping is not null && await isRetained(mapping.Identity, cancellationToken)) return;
            }
            catch (Exception failure) when (failure is IOException or JsonException) { }
        }
        await storage.InitializeAsync(cancellationToken);
        information.Refresh();
        if (!information.Exists || IsProtected(filename[..64])
            || information.LastWriteTimeUtc >= (clock.GetUtcNow() - Options.UnreferencedAssetRetention).UtcDateTime) return;
        var removedBytes = information.Length;
        File.Delete(path); storage.RecordMutation(-removedBytes);
    }
    private void CleanupTemporary(string path)
    {
        activeFiles.TryRemove(path, out _);
        try { File.Delete(path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(failure, "An abandoned cover temporary file will be retried by maintenance.");
        }
    }
    private void ProtectIdentity(string identity)
    {
        if (!ValidIdentity(identity)) return;
        var now = clock.GetUtcNow(); publicationProtection[identity] = now + Options.LeaseLifetime;
        if (publicationProtection.Count <= Options.VerificationCacheEntries) return;
        foreach (var protection in publicationProtection.Where(protection => protection.Value <= now))
            publicationProtection.TryRemove(protection);
    }
    private bool IsProtected(string identity) => publicationProtection.TryGetValue(identity, out var until) && until > clock.GetUtcNow();
    private void MovePublished(string source, string destination)
    {
        var previous = new FileInfo(destination);
        var previousBytes = previous.Exists ? previous.Length : 0;
        var currentBytes = new FileInfo(source).Length;
        File.Move(source, destination, true);
        storage.RecordMutation(currentBytes - previousBytes);
    }
    private async Task<int> ReadBodyAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Options.BodyIdleTimeout);
        return await input.ReadAsync(buffer, idle.Token);
    }
    private async Task<CoverAssetSet> BoundedAsync(Func<CancellationToken, Task<CoverAssetSet>> operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(Options.OperationTimeout);
        return await operation(timeout.Token);
    }
    private static bool ValidIdentity(string? identity) => identity is not null && identity.Length == 64 && identity.All(Uri.IsHexDigit);
    public void Dispose()
    {
        lifetime.Cancel(); cleanupFiles?.Dispose(); lifetime.Dispose();
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
    private async Task WriteJsonAsync<Value>(string path, Value value, CancellationToken cancellationToken)
    {
        await storage.InitializeAsync(cancellationToken);
        var temporary = path + Guid.NewGuid().ToString("N") + TemporaryExtension;
        activeFiles.TryAdd(temporary, 0);
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken); MovePublished(temporary, path); }
        finally { CleanupTemporary(temporary); }
    }
}
