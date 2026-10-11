using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.ConsumerCert;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;

// T26/R1 — self-contained external-consumer CERTIFICATION fixture.
// Installs TuPago3.Sdk strictly from the locally generated .nupkg (see nuget.config; no ProjectReference).
// Exercises a real gRPC client over TLS with a JWT: reads, NOT_FOUND, UnknownOutcome (Create exactly-once
// dispatch / NoRetry), cancellation and dispose. The HMAC secret is generated at runtime — no secret is
// committed to source.

var secret = new byte[32];
RandomNumberGenerator.Fill(secret);
var secretBase64 = Convert.ToBase64String(secret);

var (caPem, serverCert) = MakeCert();
var servicer = new DemoService(secretBase64);

var builder = WebApplication.CreateBuilder();
builder.Services.Configure<KestrelServerOptions>(o =>
    o.Listen(System.Net.IPAddress.Loopback, 0, lo =>
    {
        lo.Protocols = HttpProtocols.Http2;
        lo.UseHttps(serverCert);
    }));
builder.Services.AddGrpc();
builder.Services.AddSingleton(servicer);
builder.Logging.ClearProviders();

var app = builder.Build();
app.MapGrpcService<DemoService>();
await app.StartAsync();

var address = app.Urls.First();
var hostPort = new Uri(address).Host + ":" + new Uri(address).Port;
var token = LabJwt.ForgeHs512(secretBase64, "consumer", 1, new[] { "ROLE_USER" });

await using var client = new TupagoPaymentClient(new TupagoClientOptions
{
    Target = hostPort,
    UseTls = true,
    CaCertificatePem = caPem,
    ServerName = "localhost",
    Credentials = new StaticTokenProvider(token),
});

var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
Assert(perfil.Cliente.Id == 1, "GetCliente id");

var catalogo = await client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" });
Assert(catalogo.NombreCorto == "Banco Demo", "GetCatalogo");

var nf = await ThrowsAsync(() => client.GetClienteAsync(new GetClienteRequest { Id = 999 }));
Assert(TupagoErrorPredicates.HasStatusCode(nf, StatusCode.NotFound), "NOT_FOUND");

var uo = await ThrowsAsync(() => client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "ext-key" }));
Assert(TupagoErrorPredicates.IsUnknownOutcome(uo), "UnknownOutcome");
Assert(servicer.CreateQrCount == 1, "NoRetry: exactly one dispatch");

using var cts = new CancellationTokenSource();
await cts.CancelAsync();
var canc = await ThrowsAsync(() => client.GetClienteAsync(new GetClienteRequest { Id = 1 }, cts.Token));
Assert(TupagoErrorPredicates.HasStatusCode(canc, StatusCode.Cancelled) || canc is OperationCanceledException, "Cancellation");

await client.DisposeAsync();
await app.StopAsync();
Console.WriteLine("CONSUMER CERT OK: TLS + JWT + reads + NOT_FOUND + UnknownOutcome + NoRetry + Cancellation + Dispose");

static void Assert(bool cond, string what)
{
    if (!cond) throw new Exception("CONSUMER CERT ASSERTION FAILED: " + what);
    Console.WriteLine("  ok: " + what);
}

static async Task<Exception> ThrowsAsync(Func<Task> act)
{
    try { await act(); }
    catch (Exception ex) { return ex; }
    throw new Exception("CONSUMER CERT: expected exception was not thrown");
}

// ---- self-contained cert + JWT verification (consumer side; lab only) ----

static (string caPem, X509Certificate2 serverWithKey) MakeCert()
{
    using var caKey = RSA.Create(2048);
    var caReq = new CertificateRequest("CN=TUPAGO3-ConsumerCert-CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
    var ca = caReq.CreateSelfSigned(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(3650));

    using var srvKey = RSA.Create(2048);
    var srvReq = new CertificateRequest("CN=localhost", srvKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(System.Net.IPAddress.Loopback);
    srvReq.CertificateExtensions.Add(san.Build());
    srvReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    var srv = srvReq.Create(ca, ca.NotBefore.AddDays(1), ca.NotAfter.AddDays(-1), Guid.NewGuid().ToByteArray());
    var withKey = srv.CopyWithPrivateKey(srvKey);
    return (ca.ExportCertificatePem(), withKey);
}

sealed class DemoService(string secret) : PaymentService.PaymentServiceBase
{
    public int CreateQrCount { get; private set; }

    public override Task<ClientePerfil> GetCliente(GetClienteRequest request, ServerCallContext context)
    {
        RequireAuth(context);
        if (request.Id == 999)
            throw new RpcException(new Status(StatusCode.NotFound, "no such cliente"));
        return Task.FromResult(new ClientePerfil
        {
            Cliente = new Cliente { Id = request.Id, Nombre = "Cliente Demo", ClaveInstitucion = "012", Key = "KEY1" },
            CatalogoInstitucion = new CatalogoInstitucion { Id = 1, ClaveInstitucion = "012", NombreCorto = "Banco Demo" },
        });
    }

    public override Task<CatalogoInstitucion> GetCatalogo(GetCatalogoRequest request, ServerCallContext context)
    {
        RequireAuth(context);
        return Task.FromResult(new CatalogoInstitucion { Id = 1, ClaveInstitucion = request.ClaveInstitucion, NombreCorto = "Banco Demo" });
    }

    public override Task<JsonResponse> GetQrStatus(GetStatusRequest request, ServerCallContext context)
        => Task.FromResult(new JsonResponse { Json = "{}" });

    public override Task<JsonResponse> GetTelefonicoStatus(GetStatusRequest request, ServerCallContext context)
        => Task.FromResult(new JsonResponse { Json = "{}" });

    public override Task<JsonResponse> CreateQr(CreateQrRequest request, ServerCallContext context)
    {
        RequireAuth(context);
        CreateQrCount++;
        throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable for UnknownOutcome"));
    }

    public override Task<JsonResponse> CreateTelefonico(CreateTelefonicoRequest request, ServerCallContext context)
        => Task.FromResult(new JsonResponse { Json = "{}" });

    private void RequireAuth(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer "))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing bearer token"));
        if (!VerifyHs512(Convert.FromBase64String(secret), header["Bearer ".Length..]))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
    }

    private static bool VerifyHs512(byte[] key, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        var input = System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        using var hmac = new HMACSHA512(key);
        var expected = hmac.ComputeHash(input);
        var sig = LabJwt.Base64UrlDecode(parts[2]);
        if (expected.Length != sig.Length) return false;
        var diff = 0;
        for (var i = 0; i < expected.Length; i++) diff |= expected[i] ^ sig[i];
        return diff == 0;
    }
}
