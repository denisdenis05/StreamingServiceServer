using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.Images.Assets;
using Xunit;
namespace StreamingServiceServer.CoverTests;
public sealed class CoverOriginTests
{
    [Fact]
    public void VersionedLocalReferencesUseTheCurrentPublicOrigin()
    {
        var service = new ImageService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Domain"] = "https://new.invalid/", ["Cover:DomainExtension"] = "/covers/",
        }).Build());
        var filename = new string('a', 64) + "-v1-small.jpg";
        Assert.Equal("https://new.invalid/covers/" + filename, service.GetPreferredCover("https://old.invalid/images/" + filename, null, null));
        Assert.Equal("https://new.invalid/covers/legacy.jpg", service.GetPreferredCover("https://old.invalid/images/legacy.jpg", null, null));
        Assert.Equal("https://external.invalid/unmanaged.jpg", service.GetPreferredCover(null, "https://external.invalid/unmanaged.jpg", null));
    }
    [Fact]
    public async Task GeneratedUploadsCanBeRestoredAfterAnOriginChangeWithoutCallingTheOldOrigin()
    {
        using var fixture = new AssetFixture();
        var assets = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        fixture.Options.Domain = "https://new.invalid";
        File.Delete(Path.Combine(fixture.Directory, assets.Small.Filename));
        var restored = await fixture.Store.RestoreAsync(assets, CancellationToken.None);
        Assert.True(await fixture.Store.IsValidAsync(restored, CancellationToken.None));
        Assert.StartsWith(fixture.Options.Domain, fixture.Store.PublicAddress(restored.Full)); Assert.Equal(0, fixture.Handler.Requests);
    }
    [Fact]
    public async Task RestorationRejectsInvalidSourceIdentityPaths()
    {
        using var fixture = new AssetFixture(); var assets = await fixture.Store.ImportAsync(new MemoryStream(AssetFixture.Image), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.RestoreAsync(assets with { SourceIdentity = "../outside" }, CancellationToken.None));
    }
}
