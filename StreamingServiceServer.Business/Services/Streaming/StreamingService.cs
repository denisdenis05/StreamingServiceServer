using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Helpers;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public class StreamingService : IStreamingService
{
    private readonly IMetadataService _metadataService;
    private readonly IConfiguration _configuration;

    public StreamingService(IMetadataService metadataService, IConfiguration configuration)
    {
        _metadataService = metadataService;
        _configuration = configuration;
    }

    public async Task<string> GetFullStreamingPath(Guid songId, CancellationToken cancellationToken = default)
    {
        var relativePath = await GetStreamingPath(songId, cancellationToken);
        return Path.Combine(_configuration["Music:Path"]!, relativePath);
    }

    public async Task<string> GetStreamingPath(Guid songId, CancellationToken cancellationToken = default)
    {
        var record = await _metadataService.GetRecordingById(songId, Guid.Empty).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var musicRoot = _configuration["Music:Path"]
            ?? throw new InvalidOperationException("Music storage path is not configured.");
        File.GetAttributes(musicRoot);
        var relativeFolder = record == null
            ? "TEMP"
            : MusicLocationHelper.GetRelativeFolderPathForRecording(record);
        var fullFolder = Path.GetFullPath(Path.Combine(musicRoot, relativeFolder));
        var fullRoot = Path.GetFullPath(musicRoot);
        if (!Path.EndsInDirectorySeparator(fullRoot)) fullRoot += Path.DirectorySeparatorChar;
        if (!fullFolder.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("Recording folder is outside music storage.");
        try
        {
            var matchingFile = Directory.EnumerateFiles(fullFolder, songId + ".*")
                .FirstOrDefault(path => AudioFileExtensions.Contains(Path.GetExtension(path)));
            cancellationToken.ThrowIfCancellationRequested();
            if (matchingFile != null)
                return Path.GetRelativePath(musicRoot, matchingFile).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (DirectoryNotFoundException)
        {
            File.GetAttributes(musicRoot);
        }
        throw new RecordingAudioUnavailableException(songId);
    }

    private static readonly HashSet<string> AudioFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".opus", ".ogg", ".wav", ".m4a", ".aac"
    };

    public string GetContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".flac" => "audio/flac",
            ".mp3" => "audio/mpeg",
            ".opus" => "audio/opus",
            ".ogg" => "audio/ogg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            _ => "application/octet-stream"
        };
    }
}
