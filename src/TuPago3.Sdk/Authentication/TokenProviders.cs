namespace TuPago3.Sdk.Authentication;

using TuPago3.Sdk.Errors;

/// <summary>
/// Supplies a Bearer token for a gRPC call. Implementations MUST never log or expose the token.
/// Supports dynamic retrieval / rotation.
/// </summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Returns a fixed token (e.g. a synthetic dev token). Never cached or rotated.
/// </summary>
public sealed class StaticTokenProvider : ITokenProvider
{
    private readonly string _token;

    public StaticTokenProvider(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new TupagoConfigException("Credentials", "token must not be empty");
        _token = token;
    }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(_token);
}

/// <summary>
/// Supports dynamic rotation: caches a token until its `exp` claim, then refetches. If the token
/// cannot be parsed the cache is invalidated so refetch always happens. Thread-safe.
/// </summary>
public sealed class JwtTokenProvider : ITokenProvider
{
    private readonly Func<CancellationToken, Task<string>> _fetch;
    private readonly object _gate = new();
    private string _cache = "";
    private DateTime _expiry = DateTime.MinValue;

    public JwtTokenProvider(Func<CancellationToken, Task<string>> fetch)
    {
        _fetch = fetch ?? throw new TupagoConfigException("Credentials", "fetch function required");
    }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cache != "" && _expiry > DateTime.UtcNow)
                return Task.FromResult(_cache);
            if (_fetch is null)
                throw new TupagoConfigException("Credentials", "JwtTokenProvider has no fetcher");
        }

        var tok = _fetch(cancellationToken).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(tok))
            throw new TupagoConfigException("Credentials", "fetched token is empty");

        var exp = JwtInspector.ExpiryUtc(tok);
        lock (_gate)
        {
            _cache = tok;
            _expiry = exp;
        }
        return Task.FromResult(tok);
    }
}

/// <summary>
/// Reads claims (exp) from an unverified JWT payload. Does NOT validate the signature; used only
/// to schedule rotation.
/// </summary>
internal static class JwtInspector
{
    public static DateTime ExpiryUtc(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return DateTime.MinValue;
        try
        {
            var payload = parts[1];
            var pad = payload.Length % 4 == 0 ? "" : new string('=', 4 - (payload.Length % 4));
            var json = System.Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/') + pad);
            var doc = System.Text.Encoding.UTF8.GetString(json);
            var exp = ExtractLong(doc, "\"exp\"");
            return exp == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long ExtractLong(string json, string key)
    {
        var idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return 0;
        var start = json.IndexOf(':', idx) + 1;
        var end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        if (long.TryParse(json[start..end], out var v)) return v;
        return 0;
    }
}
