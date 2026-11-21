using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialMedia.Data.Models;
using StreamingServiceServer.Business.Services.Images.Assets;
using StreamingServiceServer.Business.Services.Images.Playlists;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using StreamingServiceServer.Data.Models.Library;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class PlaylistArtworkTests
{
    private const string FixtureName = "Artwork fixture";
    private const string FixtureCredential = "unused-artwork-fixture";
    private const string Domain = "https://artwork.invalid";
    private const int SourceDimension = 128;
    private const int SourceCount = 4;
    private const string SourceExtension = ".jpg";
    private const string OutputFilename = "collage.jpg";
    private static readonly string[] Colors = ["red", "green", "blue", "white"];

    [Fact]
    public void QueueDeduplicatesPendingJobsAndAllowsRefreshAfterCompletion()
    {
        var queue = new PlaylistArtworkQueue();
        var owner = Guid.NewGuid(); var playlist = Guid.NewGuid();
        queue.Enqueue(owner, playlist); queue.Enqueue(owner, playlist);
        Assert.True(queue.TryDequeue(out var request));
        Assert.False(queue.TryDequeue(out _));
        queue.Enqueue(owner, playlist);
        Assert.False(queue.TryDequeue(out _));
        queue.Complete(request!);
        queue.Enqueue(owner, playlist);
        Assert.True(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task ActualCollageKeepsQuadrantOrderAndNeverExceedsSourceDimensionsOrBytes()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var sources = new List<string>();
            for (var index = 0; index < SourceCount; index++)
            {
                var path = Path.Combine(root, index + SourceExtension);
                await RunAsync(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"color=c={Colors[index]}:s={SourceDimension}x{SourceDimension}",
                    "-vf", "noise=alls=30:allf=t", "-frames:v", "1", "-threads", "1", "-q:v", "2", path]);
                sources.Add(path);
            }
            var output = Path.Combine(root, OutputFilename);
            var renderer = new PlaylistCollageRenderer(Options.Create(new CoverAssetOptions { Path = root }));
            Assert.True(await renderer.RenderAsync(sources, output, CancellationToken.None));
            var dimensions = CoverImageAdmission.ReadDimensions(await File.ReadAllBytesAsync(output));
            Assert.InRange(dimensions.Width, 1, SourceDimension);
            Assert.InRange(dimensions.Height, 1, SourceDimension);
            Assert.Equal(dimensions.Width, dimensions.Height);
            Assert.True(new FileInfo(output).Length <= sources.Min(path => new FileInfo(path).Length));
            var pixels = await RunAsync(["-hide_banner", "-loglevel", "error", "-i", output, "-frames:v", "1", "-threads", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
            const int Channels = 3;
            const int Quarter = 4;
            var red = Channels * ((dimensions.Height / Quarter) * dimensions.Width + dimensions.Width / Quarter);
            var green = Channels * ((dimensions.Height / Quarter) * dimensions.Width + dimensions.Width * 3 / Quarter);
            var blue = Channels * ((dimensions.Height * 3 / Quarter) * dimensions.Width + dimensions.Width / Quarter);
            Assert.True(pixels[red] > pixels[red + 1] && pixels[red] > pixels[red + 2]);
            Assert.True(pixels[green + 1] > pixels[green] && pixels[green + 1] > pixels[green + 2]);
            Assert.True(pixels[blue + 2] > pixels[blue] && pixels[blue + 2] > pixels[blue + 1]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FewerThanFourCoversRepeatAvailableTilesWithinTheSameBounds(int count)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var source = Path.Combine(root, "source" + SourceExtension);
            await RunAsync(["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"testsrc2=s={SourceDimension}x{SourceDimension}",
                "-frames:v", "1", "-threads", "1", "-q:v", "2", source]);
            var renderer = new PlaylistCollageRenderer(Options.Create(new CoverAssetOptions { Path = root }));
            var output = Path.Combine(root, OutputFilename);
            Assert.True(await renderer.RenderAsync(Enumerable.Repeat(source, count).ToArray(), output, CancellationToken.None));
            Assert.True(new FileInfo(output).Length <= new FileInfo(source).Length);
            Assert.InRange(CoverImageAdmission.ReadDimensions(await File.ReadAllBytesAsync(output)).Width, 1, SourceDimension);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptyInputsDoNotGenerateImagesAndInvalidImagesAreRejected()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var renderer = new PlaylistCollageRenderer(Options.Create(new CoverAssetOptions { Path = root }));
            var output = Path.Combine(root, OutputFilename);
            Assert.False(await renderer.RenderAsync([], output, CancellationToken.None));
            var invalid = Path.Combine(root, FixtureName);
            await File.WriteAllBytesAsync(invalid, [1]);
            await Assert.ThrowsAsync<IOException>(() => renderer.RenderAsync([invalid], output, CancellationToken.None));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task FirstFourOrderedCoversAreCachedRefreshedAndIsolatedByOwner()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var owner = new User { Id = Guid.NewGuid(), Username = FixtureName, Email = Guid.NewGuid() + "@artwork.invalid", PasswordHash = FixtureCredential, ProviderId = FixtureCredential };
            var playlist = new Playlist { Id = Guid.NewGuid(), Owner = owner, Name = FixtureName };
            database.Playlists.Add(playlist);
            var paths = new List<string>();
            for (var index = 0; index <= SourceCount; index++)
            {
                var path = Path.Combine(root, index + SourceExtension);
                await File.WriteAllBytesAsync(path, AssetFixture.Image);
                paths.Add(path);
                var release = new Release { Id = Guid.NewGuid(), Title = FixtureName, Cover = string.Empty, Artist = new Artist { Id = Guid.NewGuid(), Name = FixtureName, SortName = FixtureName } };
                var recording = new Recording { Id = Guid.NewGuid(), Title = FixtureName, Cover = Domain + "/" + Path.GetFileName(path), LocalCover = Path.GetFileName(path), Release = release };
                database.Recordings.Add(recording);
                playlist.PlaylistRecordings.Add(new PlaylistRecording { RecordingId = recording.Id, Order = index, AddedById = owner.Id });
            }
            await database.SaveChangesAsync();
            var renderer = new RecordingRenderer();
            var service = new PlaylistArtworkService(database, renderer, Options.Create(new CoverAssetOptions { Path = root, Domain = Domain, MinimumFreeBytes = 0 }), fixture.Clock);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RefreshAsync(Guid.NewGuid(), playlist.Id, CancellationToken.None));
            var first = await service.RefreshAsync(owner.Id, playlist.Id, CancellationToken.None);
            Assert.Equal(paths.Take(SourceCount), renderer.Sources);
            Assert.Equal(first, await service.RefreshAsync(owner.Id, playlist.Id, CancellationToken.None));
            Assert.Equal(1, renderer.Calls);
            var removed = playlist.PlaylistRecordings.OrderBy(entry => entry.Order).First();
            database.PlaylistRecordings.Remove(removed);
            await database.SaveChangesAsync();
            renderer.BeforeComplete = async () => await database.Playlists.Where(current => current.Id == playlist.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(current => current.ArtworkVersion, Guid.NewGuid()).SetProperty(current => current.Cover, string.Empty));
            Assert.Equal(string.Empty, await service.RefreshAsync(owner.Id, playlist.Id, CancellationToken.None));
            Assert.Equal(string.Empty, await database.Playlists.AsNoTracking().Where(current => current.Id == playlist.Id).Select(current => current.Cover).SingleAsync());
            renderer.BeforeComplete = null;
            var changed = await service.RefreshAsync(owner.Id, playlist.Id, CancellationToken.None);
            Assert.NotEqual(first, changed);
            Assert.Equal(paths.Skip(1), renderer.Sources);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnsavedSelectionsReuseThePlaylistRendererAndCacheWithoutDatabaseWrites()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
        try
        {
            var paths = new List<string>();
            for (var index = 0; index <= SourceCount; index++)
            {
                var path = Path.Combine(root, index + SourceExtension);
                await File.WriteAllBytesAsync(path, AssetFixture.Image);
                paths.Add(path);
            }
            var renderer = new RecordingRenderer();
            var service = new PlaylistArtworkService(null!, renderer,
                Options.Create(new CoverAssetOptions { Path = root, Domain = Domain, MinimumFreeBytes = 0 }), TimeProvider.System);
            var covers = paths.Select(path => Domain + "/" + Path.GetFileName(path)).ToArray();
            var first = await service.PreviewAsync(covers, CancellationToken.None);
            Assert.Contains(PlaylistCollageRenderer.GeneratedPrefix, first);
            Assert.Equal(paths.Take(SourceCount), renderer.Sources);
            Assert.Equal(first, await service.PreviewAsync(covers, CancellationToken.None));
            Assert.Equal(1, renderer.Calls);
            Assert.NotEqual(first, await service.PreviewAsync(covers.Reverse().ToArray(), CancellationToken.None));
            Assert.Equal(paths.AsEnumerable().Reverse().Take(SourceCount), renderer.Sources);
            Assert.Equal(2, renderer.Calls);
            Assert.Equal(string.Empty, await service.PreviewAsync([], CancellationToken.None));
            Assert.Equal(string.Empty, await service.PreviewAsync([Domain + "/missing.jpg"], CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task ArtworkInvalidationChangesVersionAndClearsAConcurrentCoverPublish()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var owner = new User { Id = Guid.NewGuid(), Username = FixtureName, Email = Guid.NewGuid() + "@artwork.invalid", PasswordHash = FixtureCredential, ProviderId = FixtureCredential };
        var playlist = new Playlist { Id = Guid.NewGuid(), Owner = owner, Name = FixtureName };
        database.Playlists.Add(playlist);
        var recording = new PlaylistRecording { PlaylistId = playlist.Id, RecordingId = Guid.NewGuid(), Order = 0, AddedById = owner.Id };
        database.PlaylistRecordings.Add(recording);
        await database.SaveChangesAsync();
        await database.Playlists.Where(current => current.Id == playlist.Id).ExecuteUpdateAsync(update => update.SetProperty(current => current.Cover, PlaylistCollageRenderer.GeneratedPrefix + "concurrent.jpg"));
        var previous = playlist.ArtworkVersion;
        database.PlaylistRecordings.Remove(recording);
        var connection = database.Database.GetDbConnection().ConnectionString;
        using var intercepted = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().UseNpgsql(connection).AddInterceptors(new PlaylistArtworkInterceptor()).Options);
        var tracked = await intercepted.Playlists.SingleAsync(current => current.Id == playlist.Id);
        tracked.Cover = string.Empty;
        intercepted.Entry(tracked).Property(current => current.Cover).OriginalValue = string.Empty;
        var entry = await intercepted.PlaylistRecordings.SingleAsync(current => current.PlaylistId == playlist.Id);
        intercepted.PlaylistRecordings.Remove(entry);
        await intercepted.SaveChangesAsync();
        var current = await database.Playlists.AsNoTracking().SingleAsync(current => current.Id == playlist.Id);
        Assert.NotEqual(previous, current.ArtworkVersion);
        Assert.Equal(string.Empty, current.Cover);
    }

    private sealed class RecordingRenderer : IPlaylistCollageRenderer
    {
        public IReadOnlyList<string> Sources { get; private set; } = [];
        public int Calls { get; private set; }
        public Func<Task>? BeforeComplete { get; set; }
        public async Task<bool> RenderAsync(IReadOnlyList<string> sources, string output, CancellationToken cancellationToken)
        {
            Sources = sources.ToArray(); Calls++;
            await File.WriteAllBytesAsync(output, AssetFixture.Image, cancellationToken);
            if (BeforeComplete is not null) await BeforeComplete();
            return true;
        }
    }
    private static async Task<byte[]> RunAsync(string[] arguments)
    {
        var information = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) information.ArgumentList.Add(argument);
        using var process = Process.Start(information)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await copy; await errors;
        Assert.Equal(0, process.ExitCode);
        return output.ToArray();
    }
}
