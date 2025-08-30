using StreamingServiceServer.Business.Models.Authentication;

namespace StreamingServiceServer.Business.Services.Authentication;

public interface IAuthenticationService
{
    Task<LoginResponse> LoginLocalUserAsync(LocalLoginRequest request);
    Task<LoginResponse> RegisterLocalUser(LocalRegisterRequest request);
    Task<RefreshTokenResponse> RefreshJWTToken(string refreshToken);

    string GenerateAndSaveRefreshToken(Guid userId, int size = 32);
}



