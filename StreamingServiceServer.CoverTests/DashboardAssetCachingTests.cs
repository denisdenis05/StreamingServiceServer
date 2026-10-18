using Microsoft.AspNetCore.Http;
using StreamingServiceServer.API;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class DashboardAssetCachingTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("dashboard.css")]
    [InlineData("browser-player.css")]
    [InlineData("browser-player.js")]
    [InlineData("DASHBOARD.CSS")]
    public void DashboardAssetsRequireRevalidationAfterDeployment(string filename)
    {
        var context = new DefaultHttpContext();
        DashboardAssetCaching.Prepare(context, filename);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("audio.mp3")]
    public void MediaRetainsItsExistingCachePolicy(string filename)
    {
        var context = new DefaultHttpContext();
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        DashboardAssetCaching.Prepare(context, filename);
        Assert.Equal("public, max-age=31536000, immutable", context.Response.Headers.CacheControl);
    }
}
