using Microsoft.AspNetCore.DataProtection;
using StreamingServiceServer.Business.AudioAnalysis;

namespace StreamingServiceServer.API.AudioAnalysis;

public sealed class AudioAnalysisSecretStore : IAudioAnalysisSecretStore
{
    private const string ProtectionPurpose = "StreamingService.AudioAnalysis.ApiToken.v1";
    private readonly IDataProtector protector;

    public AudioAnalysisSecretStore(IDataProtectionProvider provider)
    {
        protector = provider.CreateProtector(ProtectionPurpose);
    }

    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
