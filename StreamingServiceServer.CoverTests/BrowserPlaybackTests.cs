using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using StreamingServiceServer.API;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.Services.MusicSearch;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class BrowserPlaybackTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CookieTokenStillRequiresNormalSignatureAndExpiryValidation(bool expired, bool invalidSignature, bool succeeds)
    {
        const string signingSecret = "browser-playback-fixture-signing-key-with-more-than-thirty-two-bytes";
        const string wrongSecret = "different-browser-fixture-signing-key-with-more-than-thirty-two-bytes";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("Bearer").AddJwtBearer(options =>
        {
            options.Events = new JwtBearerEvents { OnMessageReceived = BrowserStreamingAuthentication.ReadCookieAsync };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false, ValidateAudience = false, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret)),
                ValidateLifetime = true, ClockSkew = TimeSpan.Zero
            };
        });
        using var provider = services.BuildServiceProvider();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(invalidSignature ? wrongSecret : signingSecret));
        var token = new JwtSecurityToken(claims: [new Claim(ClaimTypes.NameIdentifier, "fixture")],
            notBefore: DateTime.UtcNow.AddHours(-2), expires: expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/Stream";
        context.Request.Method = "GET";
        context.Request.Headers.Cookie = BrowserStreamingAuthentication.CookieName + "=" + new JwtSecurityTokenHandler().WriteToken(token);
        Assert.Equal(succeeds, (await context.AuthenticateAsync("Bearer")).Succeeded);
    }

    [Theory]
    [InlineData("GET", "/Stream", false, "fixture")]
    [InlineData("GET", "/stream", false, "fixture")]
    [InlineData("POST", "/Stream", false, null)]
    [InlineData("GET", "/Metadata", false, null)]
    [InlineData("GET", "/Stream/browser-session", false, null)]
    [InlineData("GET", "/Stream", true, null)]
    public async Task StreamCookieIsReadOnlyForExactGetRouteWithoutAuthorizationHeader(string method, string path, bool bearerHeader, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Cookie = BrowserStreamingAuthentication.CookieName + "=fixture";
        if (bearerHeader) context.Request.Headers.Authorization = "Bearer header-token";
        var received = new MessageReceivedContext(context,
            new AuthenticationScheme("Bearer", "Bearer", typeof(JwtBearerHandler)), new JwtBearerOptions());
        await BrowserStreamingAuthentication.ReadCookieAsync(received);
        Assert.Equal(expected, received.Token);
    }

    [Fact]
    public void BrowserSessionCookieIsHttpOnlySameSiteStrictAndRestrictedToStreamPath()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers.Authorization = "Bearer fixture";
        var controller = Controller(context);
        Assert.IsType<NoContentResult>(controller.CreateBrowserSession());
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/Stream", cookie);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public void BrowserSessionCreationNeedsBearerHeaderAndRetainsControllerAuthorization()
    {
        Assert.IsType<UnauthorizedResult>(Controller(new DefaultHttpContext()).CreateBrowserSession());
        var authorization = Assert.Single(typeof(StreamController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("Bearer", authorization.AuthenticationSchemes);
        Assert.Equal("User,Admin", authorization.Roles);
        Assert.Empty(typeof(StreamController).GetMethod(nameof(StreamController.CreateBrowserSession))!.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public void ExpiredSessionCanStillClearTheScopedCookie()
    {
        var context = new DefaultHttpContext();
        Assert.IsType<NoContentResult>(Controller(context).ClearBrowserSession());
        Assert.Contains("expires=", context.Response.Headers.SetCookie.ToString());
        Assert.Contains("path=/Stream", context.Response.Headers.SetCookie.ToString());
        Assert.Single(typeof(StreamController).GetMethod(nameof(StreamController.ClearBrowserSession))!.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public async Task BrowserMissingAudioReturns404WithoutMetadataLookupOrPreviewDownload()
    {
        var streaming = new MissingAudio();
        var controller = new StreamController(streaming, null!, null!, null!);
        Assert.IsType<NotFoundObjectResult>(await controller.StreamAudio(Guid.NewGuid(), allowPreview: false));
    }

    private static StreamController Controller(HttpContext context) => new(null!, null!, null!, null!)
    {
        ControllerContext = new ControllerContext { HttpContext = context }
    };

    private sealed class MissingAudio : IStreamingService
    {
        public Task<string> GetStreamingPath(Guid songId, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new RecordingAudioUnavailableException(songId));
        public Task<string> GetFullStreamingPath(Guid songId, CancellationToken cancellationToken = default) => GetStreamingPath(songId, cancellationToken);
        public string GetContentType(string path) => "audio/mpeg";
    }
}
