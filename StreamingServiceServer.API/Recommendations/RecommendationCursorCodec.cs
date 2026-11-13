using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StreamingServiceServer.Business.Services.Recommendations;

namespace StreamingServiceServer.API.Recommendations;

public sealed class RecommendationCursorCodec(IDataProtectionProvider provider) : IRecommendationCursorCodec
{
    private const string Purpose = "StreamingService.RecommendationSession.v1";
    private const string InvalidCursor = "The recommendation session is invalid. Start a new radio session.";
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);
    public string Encode(RecommendationSession session) => protector.Protect(JsonSerializer.Serialize(session));
    public RecommendationSession Decode(string token)
    {
        try { return JsonSerializer.Deserialize<RecommendationSession>(protector.Unprotect(token)) ?? throw new RecommendationInputException(InvalidCursor); }
        catch (Exception failure) when (failure is CryptographicException or JsonException or ArgumentException)
        { throw new RecommendationInputException(InvalidCursor); }
    }
}
