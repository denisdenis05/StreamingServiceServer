using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.PreviewDownloader;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public class PreviewOptimizationTests
{
    [Fact]
    public async Task ProcessRunnerDrainsBothPipesWithoutRetainingUnboundedDiagnostics()
    {
        var process = new ProcessStartInfo("/bin/bash");
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add("for number in {1..20000}; do echo output; echo error >&2; done");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = await new PreviewProcessRunner().RunAsync(process, deadline.Token);
        Assert.Equal(8192, output.Length);
    }

    [Fact]
    public async Task ProcessRunnerCancelsSleepingChild()
    {
        var process = new ProcessStartInfo("/bin/bash");
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add("sleep 60 & wait");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PreviewProcessRunner().RunAsync(process, deadline.Token));
    }

    [Fact]
    public async Task ProcessRunnerReportsExitFailureWithBoundedDiagnostics()
    {
        var process = new ProcessStartInfo("/bin/bash");
        process.ArgumentList.Add("-c");
        process.ArgumentList.Add("echo failure >&2; exit 9");
        var exception = await Assert.ThrowsAsync<IOException>(() => new PreviewProcessRunner().RunAsync(process, default));
        Assert.Contains("9", exception.Message);
        Assert.Contains("failure", exception.Message);
    }

    [Fact]
    public async Task SharedPreviewSurvivesOneCanceledWaiterAndPublishesRelativeValidatedFile()
    {
        using var fixture = new PreviewFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeDownload = cancellationToken => release.Task.WaitAsync(cancellationToken);
        var service = fixture.CreateService();
        var recording = new RecordingResponse { Id = Guid.NewGuid(), Title = "Song", ArtistName = "Artist" };
        using var cancellation = new CancellationTokenSource();
        var first = service.DownloadPreview(recording, cancellation.Token);
        await fixture.Runner.Started.Task;
        var second = service.DownloadPreview(recording);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        var relative = await second;
        Assert.Equal("TEMP/" + recording.Id + ".mp3", relative);
        Assert.True(File.Exists(Path.Combine(fixture.Root, relative)));
        Assert.Equal(1, fixture.Runner.Downloads);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "TEMP")));
        Assert.Equal(relative, await service.DownloadPreview(recording));
        Assert.Equal(1, fixture.Runner.Downloads);
    }

    [Fact]
    public async Task FinalWaiterCancellationStopsWorkAndRemovesTemporaryDirectory()
    {
        using var fixture = new PreviewFixture();
        fixture.Runner.BeforeDownload = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        using var cancellation = new CancellationTokenSource();
        var service = fixture.CreateService();
        var work = service.DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() }, cancellation.Token);
        await fixture.Runner.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await fixture.Runner.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCleanupAsync(Path.Combine(fixture.Root, "TEMP"));
    }

    [Fact]
    public async Task DeadlineStopsAbandonedDownloader()
    {
        using var fixture = new PreviewFixture();
        fixture.Settings["Music:PreviewDownloader:DeadlineSeconds"] = "1";
        fixture.Runner.BeforeDownload = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateService().DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() }));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "TEMP")));
    }

    [Fact]
    public async Task ProcessQuotaSerializesDifferentRecordings()
    {
        using var fixture = new PreviewFixture();
        fixture.Settings["Music:PreviewDownloader:MaximumConcurrency"] = "1";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeDownload = cancellationToken => release.Task.WaitAsync(cancellationToken);
        var service = fixture.CreateService();
        var first = service.DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() });
        await fixture.Runner.Started.Task;
        var second = service.DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() });
        Assert.Equal(1, fixture.Runner.Downloads);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, fixture.Runner.MaximumActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("video")]
    public async Task InvalidAudioIsNeverPublished(string validation)
    {
        using var fixture = new PreviewFixture();
        fixture.Runner.Validation = validation;
        var recording = new RecordingResponse { Id = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateService().DownloadPreview(recording));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "TEMP")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "TEMP")));
    }

    [Fact]
    public async Task MissingStorageRootDoesNotBecomeUnavailableMedia()
    {
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => Task.FromResult<RecordingResponse>(null!));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Music:Path"] = "/tmp/nonexistent-preview-root-" + Guid.NewGuid() }).Build();
        await Assert.ThrowsAnyAsync<IOException>(() => new StreamingService(metadata, configuration).GetStreamingPath(Guid.NewGuid()));
    }

    [Fact]
    public async Task MissingMediaWithinHealthyStorageUsesTypedUnavailableResult()
    {
        using var fixture = new PreviewFixture();
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => Task.FromResult<RecordingResponse>(null!));
        var service = new StreamingService(metadata, new ConfigurationBuilder().AddInMemoryCollection(fixture.Settings).Build());
        await Assert.ThrowsAsync<RecordingAudioUnavailableException>(() => service.GetStreamingPath(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageRootWithOrWithoutTrailingSeparatorResolvesPublishedPreview(bool trailingSeparator)
    {
        using var fixture = new PreviewFixture();
        var identifier = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "TEMP"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "TEMP", identifier + ".mp3"), "fixture");
        fixture.Settings["Music:Path"] = fixture.Root + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : string.Empty);
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => Task.FromResult<RecordingResponse>(null!));
        var service = new StreamingService(metadata, new ConfigurationBuilder().AddInMemoryCollection(fixture.Settings).Build());
        Assert.Equal("TEMP/" + identifier + ".mp3", await service.GetStreamingPath(identifier));
    }

    [Fact]
    public async Task DatabaseFailureDoesNotStartMetadataOrPreviewFallback()
    {
        var streaming = DelegateProxy.Create<IStreamingService>((method, arguments) => Task.FromException<string>(new IOException("Storage unavailable")));
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => throw new InvalidOperationException("Unexpected metadata request"));
        var preview = DelegateProxy.Create<IPreviewDownloaderService>((method, arguments) => throw new InvalidOperationException("Unexpected preview request"));
        var controller = new StreamController(streaming, null!, preview, metadata);
        await Assert.ThrowsAsync<IOException>(() => controller.StreamAudio(Guid.NewGuid()));
    }

    [Fact]
    public async Task PreviewFallbackPropagatesCancellationAndEncodesOnlyRelativeSegments()
    {
        using var cancellation = new CancellationTokenSource();
        var recording = new RecordingResponse { Id = Guid.NewGuid() };
        var streaming = DelegateProxy.Create<IStreamingService>((method, arguments) => method.Name == "GetContentType" ? "audio/mpeg" : Task.FromException<string>(new RecordingAudioUnavailableException(recording.Id)));
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => Task.FromResult(recording));
        var preview = DelegateProxy.Create<IPreviewDownloaderService>((method, arguments) =>
        {
            Assert.Equal(cancellation.Token, arguments![1]);
            return Task.FromResult("TEMP/song name.mp3");
        });
        var controller = new StreamController(streaming, null!, preview, metadata)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        await controller.StreamAudio(recording.Id, cancellation.Token);
        Assert.Equal("/protected/TEMP/song%20name.mp3", controller.Response.Headers["X-Accel-Redirect"]);
    }

    [Fact]
    public async Task PendingRecordingAdmissionRejectsOverflowWithoutLaunchingAnotherProcess()
    {
        using var fixture = new PreviewFixture();
        fixture.Settings["Music:PreviewDownloader:MaximumConcurrency"] = "1";
        fixture.Runner.BeforeDownload = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        using var cancellation = new CancellationTokenSource();
        var service = fixture.CreateService();
        var pending = Enumerable.Range(0, 64).Select(number => service.DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() }, cancellation.Token)).ToArray();
        await Assert.ThrowsAsync<PreviewCapacityException>(() => service.DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() }));
        Assert.Equal(1, fixture.Runner.Downloads);
        cancellation.Cancel();
        foreach (var work in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await fixture.Runner.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCleanupAsync(Path.Combine(fixture.Root, "TEMP"));
    }

    [Fact]
    public async Task AlreadyCanceledCallerDoesNotStartPreviewWork()
    {
        using var fixture = new PreviewFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateService().DownloadPreview(new RecordingResponse { Id = Guid.NewGuid() }, cancellation.Token));
        Assert.Equal(0, fixture.Runner.Downloads);
    }

    [Fact]
    public async Task PreviewCapacityProducesRetryableHttpResponse()
    {
        var recording = new RecordingResponse { Id = Guid.NewGuid() };
        var streaming = DelegateProxy.Create<IStreamingService>((method, arguments) => Task.FromException<string>(new RecordingAudioUnavailableException(recording.Id)));
        var metadata = DelegateProxy.Create<IMetadataService>((method, arguments) => Task.FromResult(recording));
        var preview = DelegateProxy.Create<IPreviewDownloaderService>((method, arguments) => Task.FromException<string>(new PreviewCapacityException()));
        var controller = new StreamController(streaming, null!, preview, metadata)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var response = Assert.IsType<ObjectResult>(await controller.StreamAudio(recording.Id));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("10", controller.Response.Headers["Retry-After"]);
    }

    private static async Task WaitForCleanupAsync(string directory)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.GetDirectories(directory).Length != 0)
            await Task.Delay(10, cancellation.Token);
    }

    public class DelegateProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Implementation { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments) => Implementation(targetMethod!, arguments);
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> implementation) where T : class
        {
            var proxy = DispatchProxy.Create<T, DelegateProxy>();
            ((DelegateProxy)(object)proxy).Implementation = implementation;
            return proxy;
        }
    }

    private sealed class PreviewFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "preview-test-" + Guid.NewGuid());
        public Dictionary<string, string?> Settings { get; } = new();
        public FakeRunner Runner { get; } = new();
        public PreviewFixture()
        {
            Directory.CreateDirectory(Root);
            Settings["Music:Path"] = Root;
        }
        public PreviewDownloaderService CreateService() => new(new ConfigurationBuilder().AddInMemoryCollection(Settings).Build(), Runner);
        public void Dispose() { Directory.Delete(Root, true); }
    }

    private sealed class FakeRunner : IPreviewProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? BeforeDownload { get; set; }
        public string Validation { get; set; } = "audio\n";
        public int Downloads;
        public int Active;
        public int MaximumActive;
        public async Task<string> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            if (startInfo.FileName == "ffprobe") return Validation;
            Interlocked.Increment(ref Downloads);
            var active = Interlocked.Increment(ref Active);
            MaximumActive = Math.Max(MaximumActive, active);
            Started.TrySetResult();
            try
            {
                if (BeforeDownload != null) await BeforeDownload(cancellationToken);
                var output = Regex.Match(startInfo.Arguments, "\"([^\"]+)\"").Groups[1].Value;
                await File.WriteAllTextAsync(output + ".mp3", "fixture audio", cancellationToken);
                return "";
            }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
