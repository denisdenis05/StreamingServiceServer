using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace StreamingServiceServer.API;

public static class BrowserStreamingAuthentication
{
    public const string CookieName = "resonance_stream";
    public const string StreamPath = "/Stream";

    public static Task ReadCookieAsync(MessageReceivedContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals(StreamPath, StringComparison.OrdinalIgnoreCase)
            && !context.Request.Headers.ContainsKey("Authorization")
            && context.Request.Cookies.TryGetValue(CookieName, out var token)) context.Token = token;
        return Task.CompletedTask;
    }

    public static CookieOptions CookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true, Secure = request.IsHttps, SameSite = SameSiteMode.Strict, Path = StreamPath,
        IsEssential = true
    };
}
