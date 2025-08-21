namespace StreamingServiceServer.API.GraphQL.Types.Music.MusicTypes;

public class AlbumArtistInput
{
    public string Album { get; set; } = string.Empty;
    public string? Artist { get; set; }
}