using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images.Assets;

namespace StreamingServiceServer.Background;

public sealed class CoverAssetVerification(ICoverAssetStore assets, IOptions<CoverAssetOptions> configuration, TimeProvider clock)
{
    private readonly Dictionary<string, DateTimeOffset> verified = new(StringComparer.Ordinal);

    public async Task<bool> CheckAsync(CoverAssetSet asset, CancellationToken cancellationToken, DateTimeOffset? persistedNextCheck = null)
    {
        if (!await assets.IsPresentAsync(asset, cancellationToken))
        {
            verified.Remove(asset.Identity);
            return false;
        }
        var now = clock.GetUtcNow();
        if (persistedNextCheck > now) return true;
        if (verified.TryGetValue(asset.Identity, out var nextCheck) && nextCheck > now) return true;
        if (!await assets.IsValidAsync(asset, cancellationToken))
        {
            verified.Remove(asset.Identity);
            return false;
        }
        if (verified.Count >= configuration.Value.VerificationCacheEntries)
            verified.Remove(verified.MinBy(entry => entry.Value).Key);
        verified[asset.Identity] = now + configuration.Value.IntegrityInterval;
        return true;
    }
}
