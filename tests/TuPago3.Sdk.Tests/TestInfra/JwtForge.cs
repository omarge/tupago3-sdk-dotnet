using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TuPago3.Sdk.Tests.TestInfra;

/// <summary>
/// LAB-ONLY helper to forge a synthetic HS512 JWT from a base64-encoded secret. Lives in the test
/// assembly ONLY and is NOT shipped in the distributed SDK package. Mirrors the backend3 lab
/// interceptor (algorithm HS512, claims sub / userId / auth / iat / exp). This is NOT a production
/// credential minting facility; never use against real financial systems.
/// </summary>
public static class JwtForge
{
    public static string ForgeHs512(
        string base64Secret,
        string sub,
        long userId,
        string[] auth,
        TimeSpan? lifetime = null)
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

        string Seg(JsonElement e) => Base64Url(System.Text.Encoding.UTF8.GetBytes(e.GetRawText()));
        var signing = Seg(header) + "." + Seg(payload);
        using var hmac = new HMACSHA512(raw);
        var sig = Base64Url(hmac.ComputeHash(System.Text.Encoding.ASCII.GetBytes(signing)));
        return signing + "." + sig;
    }

    public static string ForgeExpired(string base64Secret, string sub, long userId, string[] auth)
        => ForgeHs512WithExp(base64Secret, sub, userId, auth, DateTimeOffset.UtcNow.AddHours(-1));

    public static string ForgeHs512WithExp(string base64Secret, string sub, long userId, string[] auth, DateTimeOffset exp)
    {
        var raw = Convert.FromBase64String(base64Secret);
        var header = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["alg"] = "HS512", ["typ"] = "JWT" });
        var now = DateTimeOffset.UtcNow;
        var payloadObj = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["userId"] = userId,
            ["auth"] = auth,
            ["iat"] = now.AddHours(-2).ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds(),
        };
        var payload = JsonSerializer.SerializeToElement(payloadObj);
        string Seg(JsonElement e) => Base64Url(Encoding.UTF8.GetBytes(e.GetRawText()));
        var signing = Seg(header) + "." + Seg(payload);
        using var hmac = new HMACSHA512(raw);
        var sig = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signing)));
        return signing + "." + sig;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
