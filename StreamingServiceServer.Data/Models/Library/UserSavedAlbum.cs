using SocialMedia.Data.Models;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Data.Models.Library;

public class UserSavedAlbum
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid ReleaseId { get; set; }
    public Release Release { get; set; } = null!;

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
