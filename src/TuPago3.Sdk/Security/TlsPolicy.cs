using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using TuPago3.Sdk.Errors;

namespace TuPago3.Sdk.Security;

/// <summary>
/// Enforces the transport-security policy:
/// <list type="bullet">
///   <item>Remote (non-loopback) targets REQUIRE TLS with certificate verification.</item>
///   <item>Plaintext is permitted ONLY for loopback addresses (127.0.0.1, ::1, localhost).</item>
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
            case "127.0.0.1":
            case "::1":
                return true;
            case "":
                return false;
        }
        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);
        // A hostname (not a literal IP) is only treated as loopback if it actually resolves to a
        // loopback address. This closes the "remote host disguised as loopback" gap: a name that
        // resolves to a non-loopback IP is NOT considered loopback and therefore requires TLS.
        return ResolvesToLoopback(host);
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
    /// Best-effort DNS resolution. Returns true ONLY when every resolved address is a loopback
    /// address. Resolution failures are treated as non-loopback (fail-closed: require TLS) except
    /// for the literal "localhost" convention handled above.
    /// </summary>
    private static bool ResolvesToLoopback(string host)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0) return false;
            foreach (var addr in addresses)
                if (!IPAddress.IsLoopback(addr)) return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Fail-closed guard: throws <see cref="TupagoConfigException"/> when a non-loopback target
    /// is configured without TLS. Plaintext loopback is explicitly permitted.
    /// </summary>
    public static void RequireTlsForTarget(string target, bool tlsEnabled)
    {
        if (!tlsEnabled && !TlsPolicy.IsLoopback(target))
        {
            throw new TupagoConfigException("Target",
                $"TLS required for non-loopback target '{target}'; plaintext is only allowed for loopback (127.0.0.1, ::1, localhost)");
        }
    }

    /// <summary>
    /// Builds <see cref="SslClientAuthenticationOptions"/> that validates the server certificate
    /// against the supplied CA PEM (private PKI) and matches the expected server name against the
    /// certificate SAN. Validation is never disabled.
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

        if (sanExt is not null)
        {
            var formatted = sanExt.Format(false);
            foreach (var line in formatted.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var value = line[(eq + 1)..].Trim();
                if (string.Equals(value, host, StringComparison.OrdinalIgnoreCase)) return true;
                if (value.StartsWith("*.") && host.EndsWith(value[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
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
