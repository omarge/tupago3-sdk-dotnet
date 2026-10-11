using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TuPago3.Sdk.Generated.V1;

namespace TuPago3.Sdk.Tests.TestInfra;

/// <summary>
/// Starts the <see cref="SimulatedPaymentService"/> on a Kestrel listener bound to 127.0.0.1 with a
/// random port. Supports plaintext (loopback dev) and real HTTPS (to exercise fail-closed TLS).
/// </summary>
public sealed class PaymentTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Address { get; }
    public SimulatedPaymentService Servicer { get; }

    private PaymentTestServer(WebApplication app, string address, SimulatedPaymentService servicer)
    {
        _app = app;
        Address = address;
        Servicer = servicer;
    }

    public static async Task<PaymentTestServer> StartAsync(SimulatedPaymentService servicer, bool useTls, System.Security.Cryptography.X509Certificates.X509Certificate2? serverCert = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(System.Net.IPAddress.Loopback, 0, lo =>
            {
                lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                if (useTls && serverCert is not null)
                    lo.UseHttps(serverCert);
            });
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(servicer);
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGrpcService<SimulatedPaymentService>();
        await app.StartAsync();

        var address = app.Urls.First();
        return new PaymentTestServer(app, address, servicer);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
