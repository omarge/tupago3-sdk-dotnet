using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using TuPago3.Sdk.Errors;

namespace TuPago3.Sdk.Security;

/// <summary>
/// Enforces the transport-security policy:
/// <list type="bullet">
///   <item>Remote (non-loopback) targets REQUIRE TLS with certificate verification.</item>
///   <item>Plaintext is permitted ONLY for explicit local identities (localhost, 127.0.0.0/8, ::1).</item>
///   <item>Arbitrary hostnames that currently resolve to loopback are NOT trusted (DNS/TOCTOU closed).</item>
///   <item>A consumer-supplied CA is supported for private PKI and is validated, never bypassed.</item>
/// </list>
/// There is no code path that disables certificate validation.
/// </summary>
public static class TlsPolicy
{
    public static bool IsLoopback(string target)
    {
        var host = ExtractHost(target).Trim().ToLowerInvariant();
        switch (host)
        {
            case "localhost":
            case "localhost.localdomain":
            case "127.0.0.1":
            case "::1":
                return true;
            case "":
                return false;
        }
        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);
        // Arbitrary hostnames are never treated as loopback, even if they currently resolve to one.
        // This closes the DNS/TOCTOU boundary.
        return false;
    }

    /// <summary>
    /// Extracts the host portion of a "host:port" target, tolerating IPv6 literals (with or without
    /// brackets) and bare IP addresses. Never mistakes the colons of an IPv6 literal for a port separator.
    /// </summary>
    private static string ExtractHost(string target)
    {
        var t = (target ?? "").Trim();
        if (t.StartsWith("["))
        {
            var close = t.IndexOf(']');
            if (close > 0) return t.Substring(1, close - 1);
        }
        if (t.Contains(':'))
        {
            // Multiple colons => IPv6 literal (no bracket), treat the whole value as the host.
            if (t.IndexOf(':') != t.LastIndexOf(':'))
                return t;
            var idx = t.LastIndexOf(':');
            return t[..idx];
        }
        return t;
    }

    /// <summary>
    /// Fail-closed guard: throws <see cref="TupagoConfigException"/> when a non-loopback target
    /// is configured without TLS. Plaintext is only allowed for explicit local identities.
    /// </summary>
    public static void RequireTlsForTarget(string target, bool tlsEnabled)
    {
        if (!tlsEnabled && !TlsPolicy.IsLoopback(target))
        {
            throw new TupagoConfigException("Target",
                $"TLS required for non-loopback target '{target}'; plaintext is only allowed for explicit local identities (localhost, 127.0.0.0/8, ::1)");
        }
    }

    /// <summary>
    /// Builds <see cref="SslClientAuthenticationOptions"/> that validates the server certificate
    /// against the supplied CA PEM (private PKI) and matches the expected server name against the
    /// certificate SAN. Validation is never disabled.
    /// Note: hostname matching currently uses a custom path; platform-native verification is preferred long-term.
    /// </summary>
    public static SslClientAuthenticationOptions BuildSslOptions(string? caCertPem, string? expectedServerName)
    {
        X509Certificate2Collection? customRoots = null;
        if (!string.IsNullOrWhiteSpace(caCertPem))
        {
            customRoots = new X509Certificate2Collection();
            foreach (var cert in ParsePemChain(caCertPem!))
                customRoots.Add(cert);
        }

        return new SslClientAuthenticationOptions
        {
            TargetHost = expectedServerName,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate is null || chain is null)
                    return false;

                using var cert = new X509Certificate2(certificate);
                if (cert.NotBefore > DateTime.UtcNow || cert.NotAfter < DateTime.UtcNow)
                    return false;

                if (!string.IsNullOrWhiteSpace(expectedServerName) && !MatchesHost(cert, expectedServerName!))
                    return false;

                var chainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                };

                if (customRoots is not null)
                {
                    chainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    foreach (X509Certificate2 root in customRoots)
                        chainPolicy.CustomTrustStore.Add(root);
                }

                using var built = new X509Chain();
                built.ChainPolicy = chainPolicy;
                return built.Build(cert);
            }
        };
    }

    private static bool MatchesHost(X509Certificate2 cert, string host)
    {
        X509SubjectAlternativeNameExtension? sanExt = null;
        foreach (var ext in cert.Extensions)
            if (ext.Oid?.Value == "2.5.29.17")
                sanExt = new X509SubjectAlternativeNameExtension(ext.RawData, false);

        // Yields the SAN value portion (after the label separator) for every entry. The SAN
        // extension string format varies across runtimes: some use "DNS Name=localhost" (one per
        // line) and others "DNS:localhost, IP Address:127.0.0.1" (comma/colon delimited). Handle
        // both '=' and ':' separators and both ',' and '\n' delimiters.
        static IEnumerable<string> SanValues(string formatted)
        {
            foreach (var part in formatted.Replace("\r", "").Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                var eq = p.IndexOf('=');
                var colon = p.IndexOf(':');
                var sep = eq >= 0 && (colon < 0 || eq < colon) ? eq : colon;
                if (sep < 0) continue;
                yield return p[(sep + 1)..].Trim();
            }
        }

        if (sanExt is not null)
        {
            foreach (var value in SanValues(sanExt.Format(true)))
            {
                if (string.Equals(value, host, StringComparison.OrdinalIgnoreCase)) return true;
                // Wildcard: *.example.com matches foo.example.com but not a.b.example.com
                if (value.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = value[1..]; // ".example.com"
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var prefix = host[..^suffix.Length];
                        if (prefix.Length > 0 && !prefix.Contains('.'))
                            return true;
                    }
                }
            }
            // When SAN exists, do not fall back to CN.
            return false;
        }

        var cn = cert.GetNameInfo(X509NameType.DnsName, false);
        if (!string.IsNullOrEmpty(cn) && string.Equals(cn, host, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static IEnumerable<X509Certificate2> ParsePemChain(string pem)
    {
        var inCert = false;
        var sb = new System.Text.StringBuilder();
        foreach (var line in pem.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Contains("-----BEGIN CERTIFICATE-----"))
            {
                inCert = true;
                sb.Clear();
            }
            else if (line.Contains("-----END CERTIFICATE-----"))
            {
                inCert = false;
                var raw = Convert.FromBase64String(sb.ToString().Replace("\n", "").Trim());
                yield return X509CertificateLoader.LoadCertificate(raw);
            }
            else if (inCert)
            {
                sb.Append(line.Trim());
            }
        }
    }
}
