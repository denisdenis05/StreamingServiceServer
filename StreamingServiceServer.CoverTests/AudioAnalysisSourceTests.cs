using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.Business.AudioAnalysis;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisSourceTests
{
    private const string MusicPathKey = "Music:Path";
    private const string TemporaryFolder = "TEMP";
    private const string FileExtension = ".flac";
    private const string RecordingTitle = "source test";
    private const string TestFolderPrefix = "audio-analysis-source-";
    private const string LinkName = "linked";
    private static readonly byte[] OriginalAudio = [11, 22, 33];
    private static readonly byte[] ReplacementAudio = [44, 55, 66];

    [Fact]
    public void RejectsPathOutsideLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Assert.False(LocalAudioAnalysisSource.IsContainedRegularFile(root, outside));
    }

    [Fact]
    public void RejectsSymbolicLinkInsideLibrary()
    {
        var root = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var filePath = Path.Combine(outside, LinkName);
            File.WriteAllBytes(filePath, OriginalAudio);
            var linkPath = Path.Combine(root, LinkName);
            File.CreateSymbolicLink(linkPath, filePath);
            Assert.False(LocalAudioAnalysisSource.IsContainedRegularFile(root, linkPath));
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [PostgresFact]
    public async Task MissingAudioRemainsUnavailableWithoutCreatingFiles()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var recordingIdentifier = await AddRecordingAsync(fixture, database);
        var root = NewDirectory();
        try
        {
            var source = await CreateSource(database, root).OpenAsync(recordingIdentifier, CancellationToken.None);
            Assert.Null(source);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [PostgresFact]
    public async Task AvailableAudioIsHashedAndReplacementsProduceNewSourceVersion()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var recordingIdentifier = await AddRecordingAsync(fixture, database);
        var root = NewDirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, TemporaryFolder));
            var path = Path.Combine(folder.FullName, recordingIdentifier + FileExtension);
            await File.WriteAllBytesAsync(path, OriginalAudio);
            var provider = CreateSource(database, root);
            string originalVersion;
            await using (var source = await provider.OpenAsync(recordingIdentifier, CancellationToken.None))
            {
                Assert.NotNull(source);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(OriginalAudio)), source.AudioSha256);
                Assert.Equal(0, source.Content.Position);
                Assert.Equal(FileExtension, source.Extension);
                originalVersion = source.SourceVersion;
            }
            await File.WriteAllBytesAsync(path, ReplacementAudio);
            await using var replacement = await provider.OpenAsync(recordingIdentifier, CancellationToken.None);
            Assert.NotNull(replacement);
            Assert.NotEqual(originalVersion, replacement.SourceVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        TestFolderPrefix + Guid.NewGuid().ToString())).FullName;

    private static LocalAudioAnalysisSource CreateSource(StreamingDbContext database, string root) => new(database,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [MusicPathKey] = root }).Build());

    private static async Task<Guid> AddRecordingAsync(DatabaseFixture fixture, StreamingDbContext database)
    {
        var releaseIdentifier = await fixture.AddReleaseAsync();
        var release = await database.Releases.SingleAsync(release => release.Id == releaseIdentifier);
        var recording = new Recording { Id = Guid.NewGuid(), Title = RecordingTitle, Cover = string.Empty, Release = release };
        database.Recordings.Add(recording);
        await database.SaveChangesAsync();
        return recording.Id;
    }
}
