namespace StreamingServiceServer.Business.Models.Library;

public static class LikedSongsPlaylistPolicy
{
    public const string RecordingLikeRoute = "likes/{recordingId}";
    public const string InvalidRecordingMessage = "Recording identifier must be a non-empty GUID.";
    public const string Route = "liked-songs-playlist";
    public const string InvalidAccountMessage = "Invalid or missing user ID in token.";
    public const string InvalidPlaylistMessage = "Playlist identifier must be a non-empty GUID or null.";
    public const string MissingPlaylistMessage = "Playlist not found or user is not the owner.";
    public const string DefaultPlaylistName = "Liked Songs";
    public const int EmptyPlaylistOrder = -1;
    public const int OrderIncrement = 1;
}
