using System.Net;
using System.Text;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Transport;
using Xunit;

namespace TuPago3.Sdk.Tests.Transport;

/// <summary>
/// Request shape tests for REST transport.
/// Captures the actual HTTP request sent by RestPaymentTransport.
/// </summary>
public sealed class T31_RequestShape : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _prefix;
    private string? _capturedPath;
    private string? _capturedIdempotency;
    private string? _capturedBody;

    public T31_RequestShape()
    {
        var port = GetFreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(CaptureLoop);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task CaptureLoop()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _capturedPath = ctx.Request.Url?.AbsolutePath;
                _capturedIdempotency = ctx.Request.Headers["Idempotency-Key"];
                using var reader = new StreamReader(ctx.Request.InputStream);
                _capturedBody = await reader.ReadToEndAsync();
                var resp = Encoding.UTF8.GetBytes("{\"mensaje\":{\"ok\":true}}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = resp.Length;
                await ctx.Response.OutputStream.WriteAsync(resp);
                ctx.Response.Close();
            }
            catch { }
        }
    }

    [Fact]
    public async Task Rest_CreateQr_SendsCorrectPath_AndIdempotencyHeader()
    {
        var uri = new Uri(_prefix);
        var opts = new TupagoClientOptions
        {
            Target = $"{uri.Host}:{uri.Port}",
            UseTls = false,
            Transport = TransportKind.Rest,
            AllowUnauthenticatedLab = true,
        };
        await using var client = new TupagoPaymentClient(opts);
        var req = new CreateQrRequest
        {
            IdCuenta = 1,
            Monto = 100.0,
            Concepto = "test",
            IdempotencyKey = "shape-key-1",
        };
        try { await client.CreateQrAsync(req); } catch { }

        Assert.Equal("/api/payments/msjpagoqrusuario", _capturedPath);
        Assert.Equal("shape-key-1", _capturedIdempotency);
        Assert.Contains("idCuenta", _capturedBody);
        Assert.Contains("monto", _capturedBody);
    }

    public void Dispose()
    {
        _listener.Stop();
    }
}
