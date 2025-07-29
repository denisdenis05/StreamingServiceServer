using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IStreamingService
{
    Task<string> GetStreamingPath(Guid songId);
}