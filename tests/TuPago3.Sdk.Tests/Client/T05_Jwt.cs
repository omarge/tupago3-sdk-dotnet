using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Client;

/// <summary>T05 ValidJwt — a correctly signed HS512 token is accepted.</summary>
public class T05_ValidJwt
{
    [Fact]
    public async Task ValidTokenAuthenticates()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        var token = JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "prodgcp", 2000, new[] { "ROLE_USER" });
        await using var client = ClientFactory.ForAddress(server.Address, token);

        var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, perfil.Cliente.Id);
        Assert.Equal("Cliente Demo", perfil.Cliente.Nombre);
    }
}

/// <summary>T06 MissingJwt — absent bearer token is rejected with UNAUTHENTICATED.</summary>
public class T06_MissingJwt
{
    [Fact]
    public async Task MissingTokenRejected()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address, token: null);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }
}

/// <summary>T07 InvalidJwt — a malformed/invalid token is rejected with UNAUTHENTICATED.</summary>
public class T07_InvalidJwt
{
    [Fact]
    public async Task InvalidTokenRejected()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address, token: "not-a-valid-token");

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetCatalogoAsync(new GetCatalogoRequest { ClaveInstitucion = "BCM" }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }
}

/// <summary>T08 TokenRotation — JwtTokenProvider caches until exp then refetches; expired tokens never cached.</summary>
public class T08_TokenRotation
{
    [Fact]
    public async Task CachesUntilExpiryThenRefetches()
    {
        var fetchCount = 0;
        Task<string> Fetch(CancellationToken _)
        {
            fetchCount++;
            return Task.FromResult(JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "u", 1, new[] { "ROLE_USER" }, TimeSpan.FromMinutes(10)));
        }
        var provider = new JwtTokenProvider(Fetch);
        var t1 = await provider.GetTokenAsync();
        var t2 = await provider.GetTokenAsync();
        Assert.Equal(t1, t2);
        Assert.Equal(1, fetchCount); // cached
    }

    [Fact]
    public async Task ExpiredTokenNeverCached()
    {
        var fetchCount = 0;
        Task<string> Fetch(CancellationToken _)
        {
            fetchCount++;
            return Task.FromResult(JwtForge.ForgeExpired(TestSecrets.SimSecretBase64, "u", 1, new[] { "ROLE_USER" }));
        }
        var provider = new JwtTokenProvider(Fetch);
        await provider.GetTokenAsync();
        await provider.GetTokenAsync();
        Assert.Equal(2, fetchCount); // refetched each time because expired
    }

    [Fact]
    public async Task RotatedTokenUsedOnWire()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);

        var calls = 0;
        Task<string> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult(JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "u", 1, new[] { "ROLE_USER" }));
        }
        await using var client = new TupagoPaymentClient(new TupagoClientOptions
        {
            Target = ClientFactory.HostPort(server.Address),
            Credentials = new JwtTokenProvider(Fetch),
        });

        var r1 = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        var r2 = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, r1.Cliente.Id);
        Assert.Equal(1, r2.Cliente.Id);
        Assert.Equal(1, calls); // cached across calls
    }
}

/// <summary>T05c DynamicPerRpc — a fresh token is fetched and injected on EVERY RPC call.</summary>
public class T05c_DynamicPerRpc
{
    [Fact]
    public async Task DistinctTokenFetchedPerCall()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);

        var n = 0;
        ITokenProvider perCall = new DynamicTokenProvider(_ => Task.FromResult(
            JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "u" + (++n), 1, new[] { "ROLE_USER" })));

        await using var client = new TupagoPaymentClient(new TupagoClientOptions
        {
            Target = ClientFactory.HostPort(server.Address),
            Credentials = perCall,
        });

        await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        await client.GetClienteAsync(new GetClienteRequest { Id = 1 });

        Assert.Equal(2, servicer.ReceivedAuthorizations.Count);
        Assert.NotEqual(servicer.ReceivedAuthorizations[0], servicer.ReceivedAuthorizations[1]);
    }
}

/// <summary>T05d ExpiredToken — an expired bearer token is rejected with UNAUTHENTICATED.</summary>
public class T05d_ExpiredToken
{
    [Fact]
    public async Task ExpiredTokenRejected()
    {
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        var expired = JwtForge.ForgeExpired(TestSecrets.SimSecretBase64, "u", 1, new[] { "ROLE_USER" });
        await using var client = ClientFactory.ForAddress(server.Address, expired);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GrpcCode);
    }
}

/// <summary>Test-only provider that returns a new token on every GetTokenAsync call.</summary>
internal sealed class DynamicTokenProvider(Func<CancellationToken, Task<string>> fetch) : ITokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => fetch(cancellationToken);
}
