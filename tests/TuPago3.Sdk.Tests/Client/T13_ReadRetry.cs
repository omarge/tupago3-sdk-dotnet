using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Client;

/// <summary>T13 BoundedReadRetry — transient read failures are retried with backoff and eventually succeed.</summary>
public class T13_BoundedReadRetry
{
    [Fact]
    public async Task TransientUnavailableIsRetriedThenSucceeds()
    {
        var servicer = new SimulatedPaymentService { UnavailableReadFailuresRemaining = 2 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
        Assert.Equal(1, perfil.Cliente.Id);
        // Two failures + one success => at least 3 server invocations (bounded retry worked).
        Assert.True(servicer.GetClienteCount >= 3);
    }
}

/// <summary>T14 ReadNonRetryableError — a definite (non-transient) read error is NOT retried.</summary>
public class T14_ReadNonRetryableError
{
    [Fact]
    public async Task NonRetryableReadThrowsImmediately()
    {
        var servicer = new SimulatedPaymentService { ReadErrorCode = Grpc.Core.StatusCode.NotFound };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetClienteAsync(new GetClienteRequest { Id = 999 }));
        Assert.Equal(Grpc.Core.StatusCode.NotFound, ex.GrpcCode);
        Assert.Equal(1, servicer.GetClienteCount); // exactly one attempt, no retry
    }
}
