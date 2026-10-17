using System.Net;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;
namespace StreamingServiceServer.CoverTests;

public sealed class CoverTransferLifetimeTests
{
    [Fact]
    public async Task CancelingTheFirstWaiterDoesNotCancelAnotherSharedSourceConsumer()
    {
        using var fixture = new AssetFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Wait = async () => { started.TrySetResult(); await release.Task; };
        using var firstCancellation = new CancellationTokenSource();
        var first = fixture.Store.GetOrCreateAsync(AssetFixture.Source, firstCancellation.Token);
        await started.Task;
        var second = fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        firstCancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(); Assert.NotNull(await second); Assert.Equal(1, fixture.Handler.Requests);
    }
    [Fact]
    public async Task FailedRenderingLeavesNoPermanentOriginalOrTemporaryFiles()
    {
        using var fixture = new AssetFixture(); fixture.Renderer.Fail = true;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.original"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.partial"));
    }
    [Fact]
    public async Task CleanupRemovesEveryAbandonedTemporaryPatternEvenForRetainedAssets()
    {
        using var fixture = new AssetFixture(); Directory.CreateDirectory(fixture.Directory);
        var identity = new string('a', 64);
        var names = new[] { Guid.NewGuid().ToString("N") + ".partial", identity + ".asset.json" + Guid.NewGuid().ToString("N") + ".partial",
            identity + ".source.json" + Guid.NewGuid().ToString("N") + ".partial", identity + "-v1-full.jpg" + Guid.NewGuid().ToString("N") + ".partial" };
        foreach (var name in names) { var path = Path.Combine(fixture.Directory, name); await File.WriteAllTextAsync(path, "fixture"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2)); }
        var recent = Path.Combine(fixture.Directory, Guid.NewGuid().ToString("N") + ".partial"); await File.WriteAllTextAsync(recent, "active grace");
        await fixture.Store.PruneAsync(new HashSet<string> { identity }, CancellationToken.None);
        Assert.Single(Directory.GetFiles(fixture.Directory)); Assert.True(File.Exists(recent));
    }
    [Fact]
    public async Task IncrementalCleanupHonorsItsFileCountBudget()
    {
        using var fixture = new AssetFixture(); fixture.Options.CleanupBatchSize = 2; Directory.CreateDirectory(fixture.Directory);
        foreach (var number in Enumerable.Range(0, 5)) { var path = Path.Combine(fixture.Directory, Guid.NewGuid().ToString("N") + ".partial"); await File.WriteAllTextAsync(path, "fixture"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2)); }
        await fixture.Store.PruneUnreferencedAsync((identity, token) => Task.FromResult(false), CancellationToken.None);
        Assert.Equal(3, Directory.GetFiles(fixture.Directory).Length);
        await fixture.Store.PruneUnreferencedAsync((identity, token) => Task.FromResult(false), CancellationToken.None);
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }
    [Fact]
    public async Task StorageAdmissionRejectsBeforeReadingAnInputWhenTheBudgetCannotFitIt()
    {
        using var fixture = new AssetFixture(); fixture.Options.MaximumStoredBytes = 1;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }
    [Fact]
    public async Task AStalledResponseBodyIsCanceledByTheIdleDeadline()
    {
        using var fixture = new AssetFixture(); fixture.Options.BodyIdleTimeout = TimeSpan.FromMilliseconds(30);
        using var handler = new TransferHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }
    [Fact]
    public async Task PermanentAndThrottledResponsesCarryDistinctFailurePolicy()
    {
        using var fixture = new AssetFixture();
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests })
        {
            using var handler = new TransferHandler(_ => { var response = new HttpResponseMessage(status); response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return response; });
            using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options), TimeProvider.System);
            var failure = await Assert.ThrowsAsync<CoverSourceFailure>(() => store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
            Assert.Equal(status == HttpStatusCode.NotFound, failure.IsPermanent); Assert.Equal(TimeSpan.FromMinutes(2), failure.RetryAfter);
        }
    }
    [Fact]
    public async Task ExpiredSourcesUseHttpValidatorsWithoutDownloadingOrRenderingAgain()
    {
        using var fixture = new AssetFixture(); fixture.Options.SourceLifetime = TimeSpan.Zero;
        var requests = 0;
        using var handler = new TransferHandler(request =>
        {
            requests++;
            if (requests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetFixture.Image) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");
                return response;
            }
            Assert.Equal("\"fixture\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options), TimeProvider.System);
        var first = await store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        var second = await store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        Assert.Equal(first, second); Assert.Equal(2, requests); Assert.Equal(3, fixture.Renderer.Dimensions.Count);
    }
    [Fact]
    public async Task ImportAdmissionRejectsExcessWaitersAndReleasesCapacityAfterCancellation()
    {
        using var fixture = new AssetFixture(); fixture.Options.MaximumPendingImports = 1;
        using var cancellation = new CancellationTokenSource(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Store.ImportAsync(new StalledStream(started), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<CoverSourceFailure>(() => fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.NotNull(await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None));
    }
    [Fact]
    public async Task WholeSourceDeadlineCancelsEvenWhenTheIdleDeadlineHasNotElapsed()
    {
        using var fixture = new AssetFixture(); fixture.Options.OperationTimeout = TimeSpan.FromMilliseconds(30);
        using var handler = new TransferHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var store = new CoverAssetStore(() => new HttpClient(handler, false), fixture.Renderer, Options.Create(fixture.Options), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }
    [Fact]
    public async Task RecentlyReusedOldAssetsSurviveCleanupWhileTheirDatabaseReferenceIsPending()
    {
        using var fixture = new AssetFixture(); fixture.Options.LeaseLifetime = TimeSpan.Zero;
        var result = await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None);
        foreach (var path in Directory.GetFiles(fixture.Directory)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));
        fixture.Options.LeaseLifetime = TimeSpan.FromMinutes(5);
        Assert.Equal(result, await fixture.Store.GetOrCreateAsync(AssetFixture.Source, CancellationToken.None));
        await fixture.Store.PruneAsync(new HashSet<string>(), CancellationToken.None);
        Assert.True(await fixture.Store.IsValidAsync(result, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.Directory, result.SourceIdentity + ".original")));
    }
    [Fact]
    public async Task MissingManifestFieldsFailValidationWithoutConstructingUnsafePaths()
    {
        using var fixture = new AssetFixture(); var assets = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        var invalid = assets with { Full = assets.Full with { Filename = null! } };
        Assert.False(await fixture.Store.IsValidAsync(invalid, CancellationToken.None));
        Assert.False(await fixture.Store.IsPresentAsync(invalid, CancellationToken.None));
    }
    [Fact]
    public async Task ManifestPublicationFailureRemovesNewlyInstalledOriginalAndVariants()
    {
        using var fixture = new AssetFixture();
        using var store = new CoverAssetStore(() => new HttpClient(fixture.Handler, false), new FailedManifestRenderer(), Options.Create(fixture.Options), TimeProvider.System);
        await Assert.ThrowsAnyAsync<IOException>(() => store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }
    private sealed class FailedManifestRenderer : ICoverImageRenderer
    {
        public async Task RenderAsync(string input, string output, int maximumDimension, CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(output, AssetFixture.Image, cancellationToken);
            var identity = Path.GetFileName(output)[..64];
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(output)!, identity + ".asset.json"));
        }
    }
    private sealed class TransferHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class StalledStream(TaskCompletionSource? started = null) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { started?.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
