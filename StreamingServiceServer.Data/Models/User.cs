using SocialMedia.Data.Models.Enums;

namespace SocialMedia.Data.Models;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; }
    public string Email { get; set; }
    public string PasswordHash { get; set; }
    public RoleStatus RoleStatus { get; set; } = RoleStatus.Unverified;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool IsEmailConfirmed { get; set; } = false;
    public LoginProvider Provider { get; set; }
    public List<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public string ProviderId { get; set; }
}