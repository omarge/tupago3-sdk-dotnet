using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TuPago3.Sdk.ConsumerCert;

/// <summary>
/// LAB-ONLY HS512 JWT forger for the self-contained consumer certification fixture. NOT part of the
/// distributed SDK. Mirrors backend3 lab claims (sub / userId / auth / iat / exp).
/// </summary>
internal static class LabJwt
{
    public static string ForgeHs512(string base64Secret, string sub, long userId, string[] auth, TimeSpan? lifetime = null)
    {
        var raw = Convert.FromBase64String(base64Secret);
        var header = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["alg"] = "HS512", ["typ"] = "JWT" });
        var now = DateTimeOffset.UtcNow;
        var payloadObj = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["userId"] = userId,
            ["auth"] = auth,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(lifetime ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds(),
        };
        var payload = JsonSerializer.SerializeToElement(payloadObj);
        string Seg(JsonElement e) => Base64Url(Encoding.UTF8.GetBytes(e.GetRawText()));
        var signing = Seg(header) + "." + Seg(payload);
        using var hmac = new HMACSHA512(raw);
        var sig = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signing)));
        return signing + "." + sig;
    }

    public static byte[] Base64UrlDecode(string s)
    {
        var pad = s.Length % 4 == 0 ? "" : new string('=', 4 - (s.Length % 4));
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + pad);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
