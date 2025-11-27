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

    public async Task<string> GetFullStreamingPath(Guid songId)
    {
        var record = await _metadataService.GetRecordingById(songId);

        if (record == null)
            throw new Exception("Record not found");

        var folderPath = Path.Combine(
            _configuration["Music:Path"],
            MusicLocationHelper.GetRelativeFolderPathForRecording(record)
        );

        if (!Directory.Exists(folderPath))
        {
            throw new FileNotFoundException($"Folder not found for recording: {folderPath}");
        }

        var matchingFile = Directory.GetFiles(folderPath, record.Id.ToString() + ".*")
            .FirstOrDefault();

        if (matchingFile == null)
        {
            throw new FileNotFoundException($"No file found for recording ID {record.Id} in {folderPath}");
        }

        var extension = Path.GetExtension(matchingFile);

        return Path.Combine(folderPath, record.Id + extension);
    }
    
    public async Task<string> GetStreamingPath(Guid songId)
    {
        var record = await _metadataService.GetRecordingById(songId);

        if (record == null)
        {
            var tempPath = CheckTempFolder(songId);
            if (tempPath != null) return tempPath;
            throw new Exception("Record not found");
        }

        var relativeFolderPath = MusicLocationHelper.GetRelativeFolderPathForRecording(record);
        
        var folderPath = Path.Combine(
            _configuration["Music:Path"],
            relativeFolderPath
        );

        if (!Directory.Exists(folderPath))
        {
            throw new FileNotFoundException($"Folder not found for recording: {folderPath}");
        }

        var matchingFile = Directory.GetFiles(folderPath, record.Id.ToString() + ".*")
            .FirstOrDefault();

        if (matchingFile == null)
        {
            throw new FileNotFoundException($"No file found for recording ID {record.Id} in {folderPath}");
        }

        var extension = Path.GetExtension(matchingFile);

        return Path.Combine(relativeFolderPath, record.Id + extension);
    }

    private string? CheckTempFolder(Guid songId)
    {
        var tempPath = Path.Combine(_configuration["Music:Path"], "TEMP");
        var matchingFile = Directory.GetFiles(tempPath, songId.ToString() + ".*")
            .FirstOrDefault();    
        
        if (matchingFile != null)
        {
            return Path.Combine($"TEMP/{songId}.mp3");
        }

        return null;
    }
}