using System.Net.Sockets;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Integration;

/// <summary>
/// Section 12 — real integration against the isolated backend3 gRPC endpoint (127.0.0.1:9090,
/// plaintext loopback). Exclusively READ RPCs. Create RPCs are never exercised here. Skipped if the
/// lab endpoint is unreachable (safe tests continue).
/// </summary>
public class Backend3RealTests
{
    private const string Address = "127.0.0.1:9090";

    private static bool Reachable()
    {
        try
        {
            using var s = new TcpClient();
            var task = s.ConnectAsync("127.0.0.1", 9090);
            return task.Wait(TimeSpan.FromSeconds(3)) && s.Connected;
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
    public async Task MissingJwt_Unauthenticated()
    {
        if (!Reachable()) return;
        await using var client = new TupagoPaymentClient(new TupagoClientOptions { Target = Address, UseTls = false });
        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }

    [Fact]
    public async Task InvalidJwt_Unauthenticated()
    {
        if (!Reachable()) return;
        await using var client = ClientFor("not-a-valid-token");
        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }

    [Fact]
    public async Task ExpiredJwt_Unauthenticated()
    {
        if (!Reachable()) return;
        var expired = JwtForge.ForgeExpired(TestSecrets.BackendSecretBase64, "prodgcp", 2000, new[] { "ROLE_USER" });
        await using var client = ClientFor(expired);
        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }

    [Fact]
    public async Task ValidJwt_GetClienteOwnership()
    {
        if (!Reachable()) return;
        await using var client = ClientFor(ValidToken());
        var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, perfil.Cliente.Id);
        Assert.False(string.IsNullOrEmpty(perfil.Cliente.Nombre));
        Assert.False(string.IsNullOrEmpty(perfil.Cliente.Key));
    }

    [Fact]
    public async Task ValidJwt_GetClienteUnknown_NotFound()
    {
        if (!Reachable()) return;
        await using var client = ClientFor(ValidToken());
        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetClienteAsync(new GetClienteRequest { Id = 999 }));
        Assert.Equal(Grpc.Core.StatusCode.NotFound, ex.GrpcCode);
    }

    [Fact]
    public async Task ValidJwt_ReadRpcsAuthenticated()
    {
        if (!Reachable()) return;
        await using var client = ClientFor(ValidToken());
        foreach (var name in new[] { "GetCatalogo", "GetQrStatus", "GetTelefonicoStatus" })
        {
            Exception? err = null;
            switch (name)
            {
                case "GetCatalogo":
                    err = await Record.ExceptionAsync(() => client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
                    break;
                case "GetQrStatus":
                    err = await Record.ExceptionAsync(() => client.GetQrStatusAsync(new GetStatusRequest { Idc = "nope", IdCuenta = 1 }));
                    break;
                case "GetTelefonicoStatus":
                    err = await Record.ExceptionAsync(() => client.GetTelefonicoStatusAsync(new GetStatusRequest { Idc = "nope", IdCuenta = 1 }));
                    break;
            }
            // The dev dataset may return NOT_FOUND, but the request must NOT be UNAUTHENTICATED.
            if (err is not null)
                Assert.False(TupagoErrorPredicates.HasStatusCode(err, Grpc.Core.StatusCode.Unauthenticated),
                    $"{name} must be authenticated (not UNAUTHENTICATED) with a valid JWT");
        }
    }
}
