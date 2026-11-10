using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;

namespace StreamingServiceServer.Business.AudioAnalysis;

public sealed class LocalAudioAnalysisSource(StreamingDbContext database, IConfiguration configuration) : IAudioAnalysisSource
{
    private const string MusicPathKey = "Music:Path";
    private const string TemporaryFolder = "TEMP";
    private const int StreamBufferBytes = 65536;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".mp3", ".opus", ".ogg", ".wav", ".m4a", ".aac" };

    public async Task<AudioAnalysisSource?> OpenAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        var musicPath = configuration[MusicPathKey];
        if (string.IsNullOrWhiteSpace(musicPath) || !Directory.Exists(musicPath)) return null;
        var recording = await database.Recordings.AsNoTracking().Where(recording => recording.Id == recordingId)
            .Select(recording => new RecordingResponse { Id = recording.Id, ArtistName = recording.Release.Artist.Name,
                ReleaseTitle = recording.Release.Title }).SingleOrDefaultAsync(cancellationToken);
        if (recording is null) return null;
        var root = Path.GetFullPath(musicPath);
        var candidates = MusicLocationHelper.EnumerateRecordingFiles(root, recording)
            .Concat(Directory.Exists(Path.Combine(root, TemporaryFolder))
                ? Directory.EnumerateFiles(Path.Combine(root, TemporaryFolder), recordingId + ".*") : []);
        var path = candidates.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileNameWithoutExtension(candidate), recordingId.ToString(), StringComparison.OrdinalIgnoreCase)
            && SupportedExtensions.Contains(Path.GetExtension(candidate)) && IsContainedRegularFile(root, candidate));
        if (path is null) return null;
        var information = new FileInfo(path);
        if (information.Length <= 0 || information.Length > AudioAnalysisProtocol.MaximumAudioBytes) return null;
        var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var fingerprint = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
            content.Position = 0;
            return new AudioAnalysisSource(content, fingerprint, fingerprint, Path.GetExtension(path));
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    public static bool IsContainedRegularFile(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains("..")) return false;
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return File.Exists(current);
    }
}
