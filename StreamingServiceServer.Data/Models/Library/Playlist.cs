using SocialMedia.Data.Models;

namespace StreamingServiceServer.Data.Models.Library;

public class Playlist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!; 

    public virtual ICollection<PlaylistRecording> PlaylistRecordings { get; set; } = new List<PlaylistRecording>();
}