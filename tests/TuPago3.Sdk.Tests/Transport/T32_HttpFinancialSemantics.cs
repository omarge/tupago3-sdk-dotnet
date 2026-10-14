using System.Net;
using System.Text;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Transport;
using Xunit;

namespace TuPago3.Sdk.Tests.Transport;

public sealed class T32_HttpFinancialSemantics
{
    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerRequest, (int Status, string Body)> _handler;
        private readonly CancellationTokenSource _cts = new();

        public LocalServer(Func<HttpListenerRequest, (int Status, string Body)> handler)
        {
            _handler = handler;
            var port = GetFreePort();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Prefix { get; }
        public int Port => new Uri(Prefix).Port;
        public int Count => Volatile.Read(ref _count);
        private int _count;

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _count);
                    var (status, body) = _handler(ctx.Request);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
            }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }

    private static TupagoClientOptions Options(int port, TransportKind kind) => new()
    {
        Target = $"127.0.0.1:{port}",
        UseTls = false,
        Transport = kind,
        Credentials = new StaticTokenProvider("test-token"),
    };

    private static CreateQrRequest Qr() => new()
    {
        IdCuenta = 1,
        Monto = 10,
        Concepto = "test",
        IdempotencyKey = "idem-test",
    };

    [Fact]
    public void DirectHttpTransports_ValidateRemotePlaintext()
    {
        var options = new TupagoClientOptions
        {
            Target = "example.invalid:80",
            UseTls = false,
            Credentials = new StaticTokenProvider("test-token"),
        };

        Assert.Throws<TupagoConfigException>(() => new RestPaymentTransport(options));
        Assert.Throws<TupagoConfigException>(() => new GraphQlPaymentTransport(options));
    }

    [Fact]
    public async Task Rest_GetCatalogo_MalformedSuccess_FailsClosed()
    {
        using var server = new LocalServer(_ => (200, "{}"));
        await using var transport = new RestPaymentTransport(Options(server.Port, TransportKind.Rest));

        var ex = await Assert.ThrowsAsync<TupagoException>(() =>
            transport.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "001" }, CancellationToken.None));

        Assert.Equal(Grpc.Core.StatusCode.Internal, ex.GrpcCode);
    }

    [Fact]
    public async Task Rest_Create503_IsUnknownOutcome_OneAttempt()
    {
        using var server = new LocalServer(_ => (503, "{\"message\":\"down\"}"));
        await using var transport = new RestPaymentTransport(Options(server.Port, TransportKind.Rest));

        await Assert.ThrowsAsync<TupagoUnknownOutcomeException>(() =>
            transport.CreateQrAsync(Qr(), CancellationToken.None));

        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task Rest_Create202_IsTypedPendingReconciliation()
    {
        using var server = new LocalServer(_ => (202, "{\"type\":\"payment-pending-reconciliation\"}"));
        await using var transport = new RestPaymentTransport(Options(server.Port, TransportKind.Rest));

        await Assert.ThrowsAsync<TupagoPendingReconciliationException>(() =>
            transport.CreateQrAsync(Qr(), CancellationToken.None));

        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task GraphQl_GetCliente_AcceptsCanonicalStringIds_AndMapsProfile()
    {
        using var server = new LocalServer(_ =>
            (200, "{\"data\":{\"cliente\":{\"cliente\":{\"id\":\"123\",\"nombre\":\"Juan\",\"claveInstitucion\":\"001\",\"key\":\"k\"},\"catalogoInstitucion\":{\"id\":\"9\",\"claveInstitucion\":\"001\",\"nombreCorto\":\"Banco\"}}}}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        var result = await transport.GetClienteAsync(
            new GetClienteRequest { Id = 123 }, CancellationToken.None);

        Assert.Equal(123, result.Cliente.Id);
        Assert.Equal("Juan", result.Cliente.Nombre);
        Assert.Equal("001", result.Cliente.ClaveInstitucion);
        Assert.Equal(9, result.CatalogoInstitucion.Id);
        Assert.Equal("Banco", result.CatalogoInstitucion.NombreCorto);
    }

    [Fact]
    public async Task GraphQl_GetCatalogo_BlankRequiredField_FailsClosed()
    {
        using var server = new LocalServer(_ =>
            (200, "{\"data\":{\"catalogoInstitucion\":{\"id\":\"9\",\"claveInstitucion\":\"001\",\"nombreCorto\":\"\"}}}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        var ex = await Assert.ThrowsAsync<TupagoException>(() =>
            transport.GetCatalogoAsync(
                new GetCatalogoRequest { ClaveInstitucion = "001" }, CancellationToken.None));

        Assert.Equal(Grpc.Core.StatusCode.Internal, ex.GrpcCode);
    }

    [Fact]
    public async Task GraphQl_Status_ReturnsCanonicalLeaf_NotEnvelope()
    {
        using var server = new LocalServer(_ =>
            (200, "{\"data\":{\"pagoQrEstatusByCuenta\":{\"status\":\"PAGADO\"}}}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        var result = await transport.GetQrStatusAsync(
            new GetStatusRequest { Idc = "IDC", IdCuenta = 7 }, CancellationToken.None);

        Assert.Contains("\"status\":\"PAGADO\"", result.Json);
        Assert.DoesNotContain("\"data\"", result.Json);
    }

    [Fact]
    public async Task GraphQl_CreatePending_IsTypedPendingReconciliation()
    {
        using var server = new LocalServer(_ =>
            (200, "{\"errors\":[{\"message\":\"pending\",\"extensions\":{\"code\":\"PAYMENT_PENDING_RECONCILIATION\"}}]}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        await Assert.ThrowsAsync<TupagoPendingReconciliationException>(() =>
            transport.CreateQrAsync(Qr(), CancellationToken.None));

        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task GraphQl_UnknownCreateError_IsUnknownOutcome()
    {
        using var server = new LocalServer(_ =>
            (200, "{\"errors\":[{\"message\":\"mystery\",\"extensions\":{\"code\":\"TOTALLY_UNKNOWN\"}}]}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        await Assert.ThrowsAsync<TupagoUnknownOutcomeException>(() =>
            transport.CreateQrAsync(Qr(), CancellationToken.None));

        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task GraphQl_Create503_IsUnknownOutcome_OneAttempt()
    {
        using var server = new LocalServer(_ => (503, "{\"message\":\"down\"}"));
        await using var transport = new GraphQlPaymentTransport(Options(server.Port, TransportKind.GraphQl));

        await Assert.ThrowsAsync<TupagoUnknownOutcomeException>(() =>
            transport.CreateQrAsync(Qr(), CancellationToken.None));

        Assert.Equal(1, server.Count);
    }
}
