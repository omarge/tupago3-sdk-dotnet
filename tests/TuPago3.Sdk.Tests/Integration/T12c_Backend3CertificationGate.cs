using System.Net.Sockets;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;
using Xunit.Abstractions;

namespace TuPago3.Sdk.Tests.Integration;

/// <summary>
/// G1 CERTIFICATION GATE — real backend3 integration (127.0.0.1:9090, plaintext loopback, READ-only).
/// This gate MUST FAIL if it does not execute successfully:
///   - If the lab endpoint is unreachable AND TUPAGO_BACKEND3_CERT=1, it fails (not skips).
///   - If reachable, it asserts concrete POSITIVE reads (ownership), NOT_FOUND for status RPCs,
///     auth rejections (missing/invalid/expired) and cross-client rejection.
/// In general CI this test is opt-in (skips when the env var is unset and the endpoint is down).
/// No Create RPC is ever issued against backend3.
/// </summary>
public class T12c_Backend3CertificationGate
{
    private const string Address = "127.0.0.1:9090";
    private readonly ITestOutputHelper _output;

    public T12c_Backend3CertificationGate(ITestOutputHelper output) => _output = output;

    private static bool Reachable()
    {
        try
        {
            using var s = new TcpClient();
            return s.ConnectAsync("127.0.0.1", 9090).Wait(TimeSpan.FromSeconds(3)) && s.Connected;
        }
        catch { return false; }
    }

    private static TupagoPaymentClient ClientFor(string token) => new(new TupagoClientOptions
    {
        Target = Address,
        UseTls = false,
        Credentials = new StaticTokenProvider(token),
    });

    private static string ValidToken() =>
        JwtForge.ForgeHs512(TestSecrets.BackendSecretBase64, "prodgcp", 2000, new[] { "ROLE_USER" });

    [Fact]
    public async Task CertificationGate_RealReads_AuthAndOwnership()
    {
        var certMode = Environment.GetEnvironmentVariable("TUPAGO_BACKEND3_CERT") == "1";
        if (!Reachable())
        {
            if (certMode)
                throw new Exception("CERT GATE FAILED: backend3 (127.0.0.1:9090) is unreachable; live certification run cannot proceed.");
            return; // opt-in skip for general CI
        }

        var token = ValidToken();

        // --- GetCliente: concrete positive read + ownership ---
        await using (var client = ClientFor(token))
        {
            var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
            Assert.Equal(1, perfil.Cliente.Id);
            Assert.False(string.IsNullOrEmpty(perfil.Cliente.Nombre), "GetCliente.Nombre must be concrete");
            Assert.False(string.IsNullOrEmpty(perfil.Cliente.Key), "GetCliente.Key must be concrete (ownership)");
            Assert.False(string.IsNullOrEmpty(perfil.CatalogoInstitucion.NombreCorto), "GetCliente.Catalogo.NombreCorto concrete");
            _output.WriteLine($"GetCliente: id={perfil.Cliente.Id} nombre='{perfil.Cliente.Nombre}' clave='{perfil.Cliente.ClaveInstitucion}' key='{perfil.Cliente.Key}' cat='{perfil.CatalogoInstitucion.NombreCorto}'");
        }

        // --- GetCatalogo: concrete positive read ---
        await using (var client = ClientFor(token))
        {
            var cat = await client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "40030" });
            Assert.Equal("40030", cat.ClaveInstitucion);
            Assert.False(string.IsNullOrEmpty(cat.NombreCorto), "GetCatalogo.NombreCorto must be concrete");
            _output.WriteLine($"GetCatalogo: clave='{cat.ClaveInstitucion}' nombreCorto='{cat.NombreCorto}' id={cat.Id}");
        }

        // --- GetQrStatus: NOT_FOUND when no message exists ---
        await using (var client = ClientFor(token))
        {
            var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
                client.GetQrStatusAsync(new GetStatusRequest { Idc = "CERT-NOPE", IdCuenta = 1 }));
            Assert.Equal(Grpc.Core.StatusCode.NotFound, ex.GrpcCode);
            _output.WriteLine("GetQrStatus: NOT_FOUND (no message) — OK");
        }

        // --- GetTelefonicoStatus: NOT_FOUND when no message exists ---
        await using (var client = ClientFor(token))
        {
            var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
                client.GetTelefonicoStatusAsync(new GetStatusRequest { Idc = "CERT-NOPE", IdCuenta = 1 }));
            Assert.Equal(Grpc.Core.StatusCode.NotFound, ex.GrpcCode);
            _output.WriteLine("GetTelefonicoStatus: NOT_FOUND (no message) — OK");
        }

        // --- Missing / Invalid / Expired JWT rejected (UNAUTHENTICATED) ---
        foreach (var (label, tk) in new[] { ("missing", (string?)null), ("invalid", "eyJhbGciOiJIUzUxMiJ9.eyJzdWIiOiJ4In0.invalid"), ("expired", JwtForge.ForgeExpired(TestSecrets.BackendSecretBase64, "prodgcp", 2000, new[] { "ROLE_USER" })) })
        {
            await using var c = tk is null ? new TupagoPaymentClient(new TupagoClientOptions { Target = Address, UseTls = false }) : ClientFor(tk);
            var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
                c.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "40030" }));
            Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
            _output.WriteLine($"Auth[{label}]: UNAUTHENTICATED — OK");
        }

        // --- Cross-client access rejected (does NOT return another client's data) ---
        await using (var client = ClientFor(token))
        {
            var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
                client.GetClienteAsync(new GetClienteRequest { Id = 2 }));
            Assert.True(
                ex.GrpcCode == Grpc.Core.StatusCode.NotFound || ex.GrpcCode == Grpc.Core.StatusCode.PermissionDenied,
                $"cross-client access must be rejected, got {ex.GrpcCode}");
            _output.WriteLine($"Cross-client GetCliente(2): {ex.GrpcCode} — rejected — OK");
        }
    }
}
