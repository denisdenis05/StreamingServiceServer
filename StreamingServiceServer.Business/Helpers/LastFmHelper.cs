using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace StreamingServiceServer.Business.Helpers;

public static class LastFmHelper
{
    public static string GenerateApiSignature(Dictionary<string, string> parameters, string apiSecret)
    {
        // 1. Sort parameters alphabetically by key
        var sorted = parameters.OrderBy(p => p.Key);

        // 2. Concatenate key + value
        var sb = new StringBuilder();
        foreach (var p in sorted)
            sb.Append(p.Key).Append(p.Value);

        // 3. Append secret
        sb.Append(apiSecret);

        // 4. MD5 hash
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));

        // 5. Convert to lowercase hex
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }
    
    public static string BuildQueryString(Dictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(p => 
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"
        ));
    }

}