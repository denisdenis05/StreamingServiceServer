namespace StreamingServiceServer.API;

public static class DashboardAssetCaching
{
    public static void Prepare(HttpContext context, string filename)
    {
        var extension = Path.GetExtension(filename);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)) context.Response.Headers.CacheControl = "no-cache";
    }
}
