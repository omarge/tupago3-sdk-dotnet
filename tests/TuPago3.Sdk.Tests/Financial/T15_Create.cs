using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Financial;

/// <summary>T15 CreateQrSingleAttempt — a successful CreateQr dispatches exactly once.</summary>
public class T15_CreateQrSingleAttempt
{
    [Fact]
    public async Task ExactlyOneDispatch()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var resp = await client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "q1" });
        Assert.Contains("q1", resp.Json);
        Assert.Equal(1, servicer.CreateQrCount);
    }
}

/// <summary>T16 CreateTelefonicoSingleAttempt — a successful CreateTelefonico dispatches exactly once.</summary>
public class T16_CreateTelefonicoSingleAttempt
{
    [Fact]
    public async Task ExactlyOneDispatch()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var resp = await client.CreateTelefonicoAsync(new CreateTelefonicoRequest { Key = "k", Monto = 50, IdempotencyKey = "t1" });
        Assert.Contains("t1", resp.Json);
        Assert.Equal(1, servicer.CreateTelefonicoCount);
    }
}

/// <summary>T17 CreateUnavailableUnknownOutcome — UNAVAILABLE after a sent request maps to UnknownOutcome (no retry).</summary>
public class T17_CreateUnavailableUnknownOutcome
{
    [Fact]
    public async Task UnknownOutcomeNotRetried()
    {
        var servicer = new SimulatedPaymentService { UnavailableOnCreate = true };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "q2" }));
        Assert.True(TupagoErrorPredicates.IsUnknownOutcome(ex));
        Assert.Equal(1, servicer.CreateQrCount); // single attempt, no blind retry
    }
}

/// <summary>T18 CreateDeadlineUnknownOutcome — DEADLINE_EXCEEDED after a sent request maps to UnknownOutcome.</summary>
public class T18_CreateDeadlineUnknownOutcome
{
    [Fact]
    public async Task DeadlineUnknownOutcome()
    {
        var servicer = new SimulatedPaymentService { DeadlineOnCreate = true, SlowCreateSeconds = 3 };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address, deadline: TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "q3" }));
        Assert.True(TupagoErrorPredicates.IsUnknownOutcome(ex));
        Assert.Equal(1, servicer.CreateQrCount);
    }
}

/// <summary>T19 PendingReconciliation — ABORTED carrying the marker maps to PendingReconciliationException.</summary>
public class T19_PendingReconciliation
{
    [Fact]
    public async Task MapsToPendingReconciliation()
    {
        var servicer = new SimulatedPaymentService { PendingReconciliationOnCreate = true };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = "q4" }));
        Assert.True(TupagoErrorPredicates.IsPendingReconciliation(ex));
        Assert.Equal(1, servicer.CreateQrCount);
    }
}

/// <summary>T20 IdempotencyKeyPreserved — the idempotency key is sent verbatim and never regenerated.</summary>
public class T20_IdempotencyKeyPreserved
{
    [Fact]
    public async Task KeySentVerbatim()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        const string key = "verbatim-idempotency-key-123";
        await client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100, IdempotencyKey = key });
        await client.CreateTelefonicoAsync(new CreateTelefonicoRequest { Key = "k", Monto = 50, IdempotencyKey = "tel-" + key });

        Assert.Single(servicer.ReceivedQrKeys);
        Assert.Equal(key, servicer.ReceivedQrKeys[0]);
        Assert.Equal("tel-" + key, servicer.ReceivedTelefonicoKeys[0]);
    }

    [Fact]
    public async Task MissingKeyRejectedBeforeSend()
    {
        var servicer = new SimulatedPaymentService();
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        await Assert.ThrowsAsync<TupagoConfigException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 100 }));
        Assert.Equal(0, servicer.CreateQrCount);
    }
}
