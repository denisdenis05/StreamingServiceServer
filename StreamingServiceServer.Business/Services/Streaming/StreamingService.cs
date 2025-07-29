using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;
using Microsoft.EntityFrameworkCore;



public class StreamingService : IStreamingService
{
    private readonly IMetadataService _metadataService;
    private readonly IConfiguration _configuration;
    
    public StreamingService(IMetadataService metadataService, IConfiguration configuration)
    {
        _metadataService = metadataService;
        _configuration = configuration;
    }

    public async Task<string> GetStreamingPath(Guid songId)
    {
        var record = await _metadataService.GetRecordingById(songId);
        
        if(record == null)
            throw new Exception("Record not found");
        
        return _configuration["Music:Path"] + GetRelativePathForRecording(record);
    }

    private string GetRelativePathForRecording(RecordingResponse recording)
    {
        var artistFolder = recording.ArtistName.Replace(" ", "%").ToUpper();
        var albumFolder = recording.ReleasseTitle.Replace(" ", "%").ToUpper();
        var song = recording.Id.ToString() + ".mp3";
        
        return $"{artistFolder}/{albumFolder}/{song}";
    }
}