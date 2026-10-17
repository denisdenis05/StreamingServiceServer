using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.MusicSearch;

public interface IStreamingService
{
    Task<string> GetFullStreamingPath(Guid songId, CancellationToken cancellationToken = default);
    Task<string> GetStreamingPath(Guid songId, CancellationToken cancellationToken = default);
    string GetContentType(string path);
}
