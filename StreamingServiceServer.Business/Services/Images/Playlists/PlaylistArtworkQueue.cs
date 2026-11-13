using System.Collections.Concurrent;

namespace StreamingServiceServer.Business.Services.Images.Playlists;

public sealed record PlaylistArtworkRequest(Guid UserId, Guid PlaylistId);
public sealed class PlaylistArtworkQueue
{
    private const int MaximumPending = 1024;
    private readonly ConcurrentQueue<PlaylistArtworkRequest> pending = new();
    private readonly ConcurrentDictionary<PlaylistArtworkRequest, byte> admitted = new();
    public void Enqueue(Guid userId, Guid playlistId)
    {
        var request = new PlaylistArtworkRequest(userId, playlistId);
        if (pending.Count < MaximumPending && admitted.TryAdd(request, default)) pending.Enqueue(request);
    }
    public bool TryDequeue(out PlaylistArtworkRequest? request) => pending.TryDequeue(out request);
    public void Complete(PlaylistArtworkRequest request) => admitted.TryRemove(request, out _);
}
