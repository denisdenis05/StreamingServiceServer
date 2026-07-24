using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace StreamingServiceServer.Business.Helpers;

public static class LastFmHelper
{
    public static string GenerateApiSignature(Dictionary<string, string> parameters, string apiSecret)
    {

        var sorted = parameters.OrderBy(p => p.Key);


        var sb = new StringBuilder();
        foreach (var p in sorted)
            sb.Append(p.Key).Append(p.Value);


        sb.Append(apiSecret);


        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));


        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    public static string BuildQueryString(Dictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(p => 
            $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"
        ));
    }

}
