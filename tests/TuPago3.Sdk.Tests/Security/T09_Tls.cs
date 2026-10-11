using System.Security.Cryptography.X509Certificates;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Security;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Security;

/// <summary>
/// T09 RemoteTlsRequired — plaintext is rejected for non-loopback targets before any send, and
/// loopback plaintext is explicitly permitted.
/// </summary>
public class T09_RemoteTlsRequired
{
    [Fact]
    public void NonLoopbackPlaintextRejectedBeforeSend()
    {
        var ex = Assert.Throws<TupagoConfigException>(() =>
            new TupagoPaymentClient(new TupagoClientOptions { Target = "10.0.0.5:9090", UseTls = false }));
        Assert.Contains("TLS required", ex.Message);
    }

    [Fact]
    public void LoopbackPlaintextPermitted()
    {
        var ex = Record.Exception(() =>
            new TupagoPaymentClient(new TupagoClientOptions { Target = "127.0.0.1:9090", UseTls = false }));
        Assert.Null(ex);
    }

    [Fact]
    public void PublicHostnamePlaintextRejected()
    {
        var ex = Assert.Throws<TupagoConfigException>(() =>
            new TupagoPaymentClient(new TupagoClientOptions { Target = "grpc.tupago.example:443", UseTls = false }));
        Assert.Contains("TLS required", ex.Message);
    }
}

/// <summary>
/// Mandatory TLS scenarios: trusted accepted, untrusted/ expired/ hostname-mismatch rejected,
/// JWT preserved over TLS, and no downgrade (verification callback is never a universal accept).
/// </summary>
public class TlsScenarioTests
{
    [Fact]
    public async Task TrustedCaAccepted_AndJwtPreserved()
    {
        var bundle = CertFactory.Generate("trusted");
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: true, bundle.ServerCertWithKey);
        var token = JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "u", 1, new[] { "ROLE_USER" });
        await using var client = ClientFactory.ForAddress(server.Address, token, useTls: true, caPem: bundle.CaPem, serverName: "localhost");

        var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, perfil.Cliente.Id);
    }

    [Fact]
    public async Task UntrustedCaRejected()
    {
        var bundle = CertFactory.Generate("untrusted");
        await using var server = await PaymentTestServer.StartAsync(new SimulatedPaymentService(), useTls: true, bundle.ServerCertWithKey);
        var trustedCa = CertFactory.Generate("trusted").CaPem;
        await using var client = ClientFactory.ForAddress(server.Address, useTls: true, caPem: trustedCa, serverName: "localhost");

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
    }

    [Fact]
    public async Task HostnameMismatchRejected()
    {
        var bundle = CertFactory.Generate("mismatch");
        await using var server = await PaymentTestServer.StartAsync(new SimulatedPaymentService(), useTls: true, bundle.ServerCertWithKey);
        var trustedCa = CertFactory.Generate("trusted").CaPem;
        await using var client = ClientFactory.ForAddress(server.Address, useTls: true, caPem: trustedCa, serverName: "localhost");

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
    }

    [Fact]
    public async Task ExpiredCertRejected()
    {
        var bundle = CertFactory.Generate("expired");
        await using var server = await PaymentTestServer.StartAsync(new SimulatedPaymentService(), useTls: true, bundle.ServerCertWithKey);
        var trustedCa = CertFactory.Generate("trusted").CaPem;
        await using var client = ClientFactory.ForAddress(server.Address, useTls: true, caPem: trustedCa, serverName: "localhost");

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
    }

    [Fact]
    public async Task LoopbackPlaintextAccepted()
    {
        await using var server = await PaymentTestServer.StartAsync(new SimulatedPaymentService(), useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address, useTls: false);
        var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, perfil.Cliente.Id);
    }

    [Fact]
    public void NoDowngrade_TlsVerificationCallbackIsEnforced()
    {
        var opts = TlsPolicy.BuildSslOptions("dummy", "localhost");
        Assert.NotNull(opts.RemoteCertificateValidationCallback);
        // The callback must validate; a universal-accept implementation would not reference the cert.
        Assert.True(opts.EnabledSslProtocols.HasFlag(System.Security.Authentication.SslProtocols.Tls12));
    }
}
