using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TuPago3.Sdk.Tests.TestInfra;

/// <summary>
/// Generates self-contained PEM fixtures to exercise the fail-closed TLS rules without external
/// dependencies:
/// <list type="bullet">
///   <item>trusted   — server cert signed by the trusted CA, SAN localhost/127.0.0.1 — accepted.</item>
///   <item>untrusted — server cert signed by a DIFFERENT CA — rejected (unknown CA).</item>
///   <item>expired   — server cert signed by the trusted CA but expired — rejected (expired).</item>
///   <item>mismatch  — server cert signed by the trusted CA but SAN=otherhost — rejected (mismatch).</item>
/// </list>
/// </summary>
public static class CertFactory
{
    public sealed record Bundle(string CaPem, string ServerCertPem, string ServerKeyPem, X509Certificate2 ServerCertWithKey);

    public static Bundle Generate(string kind)
    {
        var san = new List<string> { "localhost", "127.0.0.1" };

        var (caKey, caCert) = BuildCa("TUPAGO3-Test-CA");

        (RSA srvKey, X509Certificate2 srvCert) pair;
        switch (kind)
        {
            case "trusted":
                pair = BuildServer(caKey, caCert, "TUPAGO3-Test-CA", "localhost", san);
                break;
            case "untrusted":
                var (otherKey, otherCert) = BuildCa("TUPAGO3-Other-CA");
                pair = BuildServer(otherKey, otherCert, "TUPAGO3-Other-CA", "localhost", san);
                break;
            case "expired":
                // Expired relative to NOW but strictly inside the CA validity window.
                pair = BuildServer(caKey, caCert, "TUPAGO3-Test-CA", "localhost", san,
                    notBefore: caCert.NotBefore.AddDays(1), notAfter: DateTime.UtcNow.AddHours(-12));
                break;
            case "mismatch":
                pair = BuildServer(caKey, caCert, "TUPAGO3-Test-CA", "otherhost", new List<string> { "otherhost" });
                break;
            default:
                throw new ArgumentException($"unknown TLS scenario: {kind}");
        }

        var srvWithKey = pair.srvCert.CopyWithPrivateKey(pair.srvKey);
        return new Bundle(
            caCert.ExportCertificatePem(),
            pair.srvCert.ExportCertificatePem(),
            pair.srvKey.ExportPkcs8PrivateKeyPem(),
            srvWithKey);
    }

    private static (RSA key, X509Certificate2 cert) BuildCa(string cn)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var notBefore = DateTime.UtcNow.AddDays(-2);
        var notAfter = DateTime.UtcNow.AddDays(3650);
        var cert = req.CreateSelfSigned(notBefore, notAfter);
        return (key, cert);
    }

    private static (RSA key, X509Certificate2 cert) BuildServer(
        RSA caKey, X509Certificate2 caCert, string caCn, string cn, List<string> san,
        DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var s in san)
        {
            if (IPAddress.TryParse(s, out var ip))
                sanBuilder.AddIpAddress(ip);
            else
                sanBuilder.AddDnsName(s);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        // Keep the server certificate strictly inside the CA validity window.
        var nb = notBefore ?? caCert.NotBefore.AddDays(1);
        var na = notAfter ?? caCert.NotAfter.AddDays(-1);
        if (nb < caCert.NotBefore) nb = caCert.NotBefore.AddDays(1);
        if (na > caCert.NotAfter) na = caCert.NotAfter.AddDays(-1);
        if (na <= nb) na = nb.AddDays(1);
        var cert = req.Create(caCert, nb, na, Guid.NewGuid().ToByteArray());
        return (key, cert);
    }
}
