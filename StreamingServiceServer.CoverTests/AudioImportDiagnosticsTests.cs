using System.Text.Json;
using StreamingServiceDownloader.Importing;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioImportDiagnosticsTests
{
    [Fact]
    public async Task NewSourceOrImportGenerationCannotInheritAnOlderFailure()
    {
        using var fixture = new DiagnosticsFixture();
        var diagnostics = new AudioImportDiagnostics(fixture.Root, fixture.Source);
        await Assert.ThrowsAsync<IOException>(() => diagnostics.TrackAsync(
            report => Task.FromException<AudioImportResult>(new IOException("old source unavailable")), default));
        Assert.Equal("old source unavailable", (await diagnostics.ReadAsync(default))!.LastError);
        var replacement = new PendingDownload { Id = fixture.Source.Id, SourceName = fixture.Source.SourceName };
        Assert.Null(await new AudioImportDiagnostics(fixture.Root, replacement).ReadAsync(default));
        replacement.ImportIdentifier = fixture.Source.ImportIdentifier;
        replacement.SourceName = "replacement-source";
        Assert.Null(await new AudioImportDiagnostics(fixture.Root, replacement).ReadAsync(default));
    }

    [Fact]
    public async Task ProgressStorageFailureCannotPreventImportOrCreateAnAbsentMusicRoot()
    {
        using var fixture = new DiagnosticsFixture();
        Directory.Delete(fixture.Root);
        var errors = new List<Exception>();
        var diagnostics = new AudioImportDiagnostics(fixture.Root, fixture.Source, errors.Add);
        var result = await diagnostics.TrackAsync(report => Task.FromResult(new AudioImportResult(1, 1, true)), default);
        Assert.True(result.Complete);
        Assert.Single(errors);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task CorruptStatusProducesAReadErrorInsteadOfPretendingAnAlbumCompleted()
    {
        using var fixture = new DiagnosticsFixture();
        var diagnostics = new AudioImportDiagnostics(fixture.Root, fixture.Source);
        await diagnostics.TrackAsync(report => Task.FromResult(new AudioImportResult(1, 0, false)), default);
        var path = Directory.GetFiles(fixture.Root, "*.status.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(path, "invalid json");
        await Assert.ThrowsAsync<JsonException>(() => diagnostics.ReadAsync(default));
    }

    [Fact]
    public async Task OversizedFailureMessageIsBoundedAndRemainsReadable()
    {
        using var fixture = new DiagnosticsFixture();
        var diagnostics = new AudioImportDiagnostics(fixture.Root, fixture.Source);
        var failure = new IOException(new string('x', AudioImportDiagnostics.MaximumErrorCharacters + 1));
        await Assert.ThrowsAsync<IOException>(() => diagnostics.TrackAsync(
            report => Task.FromException<AudioImportResult>(failure), default));
        Assert.Equal(AudioImportDiagnostics.MaximumErrorCharacters, (await diagnostics.ReadAsync(default))!.LastError!.Length);
    }

    [Fact]
    public async Task WrappedFailureRetainsItsUnderlyingCause()
    {
        using var fixture = new DiagnosticsFixture();
        var diagnostics = new AudioImportDiagnostics(fixture.Root, fixture.Source);
        var failure = new IOException("Could not update the queue.", new IOException("Database connection refused."));
        await Assert.ThrowsAsync<IOException>(() => diagnostics.TrackAsync(
            report => Task.FromException<AudioImportResult>(failure), default));
        Assert.Contains("Database connection refused", (await diagnostics.ReadAsync(default))!.LastError);
    }

    private sealed class DiagnosticsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "import-status-tests-" + Guid.NewGuid());
        public PendingDownload Source { get; } = new() { Id = Guid.NewGuid(), SourceName = "source" };
        public DiagnosticsFixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
