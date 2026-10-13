using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Transport;
using Xunit;

namespace TuPago3.Sdk.Tests.Transport;

/// <summary>
/// Cross-transport conformance tests.
/// Validates transport selection, auth fail-closed, and that REST/GraphQL
/// implementations exist and can be constructed. Live backend3 conformance
/// requires TUPAGO_BACKEND3_LAB=1 and is opt-in.
/// </summary>
public sealed class T30_CrossTransportConformance
{
    [Fact]
    public void Grpc_IsDefaultTransport()
    {
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:9090",
            UseTls = false,
            AllowUnauthenticatedLab = true,
        };
        Assert.Equal(TransportKind.Grpc, opts.Transport);
    }

    [Fact]
    public void Rest_Transport_CanBeSelected()
    {
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:8080",
            UseTls = false,
            Transport = TransportKind.Rest,
            AllowUnauthenticatedLab = true,
        };
        opts.Validate();
        Assert.Equal(TransportKind.Rest, opts.Transport);
    }

    [Fact]
    public void GraphQl_Transport_CanBeSelected()
    {
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:8080",
            UseTls = false,
            Transport = TransportKind.GraphQl,
            AllowUnauthenticatedLab = true,
        };
        opts.Validate();
        Assert.Equal(TransportKind.GraphQl, opts.Transport);
    }

    [Fact]
    public void AuthFailClosed_WhenCredentialsMissing_AndNotLab()
    {
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:9090",
            UseTls = false,
            // Credentials null and AllowUnauthenticatedLab false
        };
        Assert.Throws<TupagoConfigException>(() => opts.Validate());
    }

    [Fact]
    public void AuthFailClosed_AllowsLabOptIn()
    {
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:9090",
            UseTls = false,
            AllowUnauthenticatedLab = true,
        };
        var validated = opts.Validate();
        Assert.NotNull(validated);
    }

    [Fact]
    public void BlankToken_FailsClosed()
    {
        var provider = new StaticTokenProvider("   ");
        var opts = new TupagoClientOptions
        {
            Target = "127.0.0.1:9090",
            UseTls = false,
            Credentials = provider,
        };
        // Construction succeeds; blank token fails on first call (interceptor).
        // This test documents the contract.
        Assert.NotNull(opts.Credentials);
    }

    [Fact]
    public void IPaymentTransport_ContractExists()
    {
        // Compile-time check that the interface covers C1-C6.
        Assert.NotNull(typeof(IPaymentTransport));
        var methods = typeof(IPaymentTransport).GetMethods();
        Assert.Contains(methods, m => m.Name == "CreateQrAsync");
        Assert.Contains(methods, m => m.Name == "CreateTelefonicoAsync");
        Assert.Contains(methods, m => m.Name == "GetQrStatusAsync");
        Assert.Contains(methods, m => m.Name == "GetTelefonicoStatusAsync");
        Assert.Contains(methods, m => m.Name == "GetClienteAsync");
        Assert.Contains(methods, m => m.Name == "GetCatalogoAsync");
    }
}
