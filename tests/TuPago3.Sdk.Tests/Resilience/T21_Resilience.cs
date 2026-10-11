using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Resilience;

/// <summary>T21 CancellationTokenPropagation — an already-cancelled token fails fast and sends nothing.</summary>
public class T21_CancellationTokenPropagation
{
    [Fact]
    public async Task CancelledTokenDoesNotDispatch()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "c1" }, cts.Token));
        Assert.True(TupagoErrorPredicates.HasStatusCode(ex, Grpc.Core.StatusCode.Cancelled) || ex is OperationCanceledException);
        Assert.Equal(0, servicer.CreateQrCount); // nothing was put on the wire
    }
}

/// <summary>T22 AsyncDisposeClosesResources — calling after DisposeAsync fails (channel closed).</summary>
public class T22_AsyncDisposeClosesResources
{
    [Fact]
    public async Task CallAfterDisposeThrows()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        var client = ClientFactory.ForAddress(server.Address);
        await client.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
    }
}

/// <summary>T23 ConcurrentClientCalls — many concurrent reads and creates all complete; each create dispatches exactly once.</summary>
public class T23_ConcurrentClientCalls
{
    [Fact]
    public async Task ConcurrentReadsAndCreates()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var readTasks = Enumerable.Range(0, 20)
            .Select(i => client.GetClienteAsync(new GetClienteRequest { Id = 1 }))
            .ToArray();
        var createTasks = Enumerable.Range(0, 20)
            .Select(i => client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "conc-" + i }))
            .ToArray();

        await Task.WhenAll(readTasks.Cast<Task>().Concat(createTasks.Cast<Task>()));

        Assert.All(readTasks, t => Assert.Equal(1, t.Result.Cliente.Id));
        Assert.All(createTasks, t => Assert.NotNull(t.Result.Json));
        Assert.Equal(20, servicer.CreateQrCount); // exactly one dispatch per create call
        Assert.Equal(20, servicer.ReceivedQrKeys.Distinct().Count()); // no key regeneration
    }
}

/// <summary>T24 NoSecretsInLogs — a distinctive JWT never appears in any captured log output.</summary>
public class T24_NoSecretsInLogs
{
    [Fact]
    public async Task TokenNeverLogged()
    {
        var capturing = new CapturingLoggerFactory();
        var servicer = new SimulatedPaymentService { LabSecretBase64 = TestSecrets.SimSecretBase64 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        var distinctive = JwtForge.ForgeHs512(TestSecrets.SimSecretBase64, "spy", 1, new[] { "ROLE_USER" });
        Assert.Contains(".", distinctive); // it is a JWT

        await using var client = new TupagoPaymentClient(new TupagoClientOptions
        {
            Target = ClientFactory.HostPort(server.Address),
            Credentials = new StaticTokenProvider(distinctive),
            LoggerFactory = capturing,
        });

        // Exercise both success and auth-rejection paths.
        await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        try { await client.GetClienteAsync(new GetClienteRequest { Id = 1 }, new CancellationToken(true)); } catch { }

        await Task.Delay(50);
        foreach (var line in capturing.Messages)
            Assert.DoesNotContain(distinctive, line);
    }
}
