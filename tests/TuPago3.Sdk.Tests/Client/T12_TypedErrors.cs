using Grpc.Core;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Tests.TestInfra;
using Xunit;

namespace TuPago3.Sdk.Tests.Client;

/// <summary>
/// T12 TypedGrpcErrors — every mapped gRPC status code is surfaced as a typed TupagoException that
/// preserves the status code and is inspectable via the predicates.
/// </summary>
public class T12_TypedGrpcErrors
{
    private static readonly StatusCode[] _codes =
    {
        StatusCode.NotFound, StatusCode.Unauthenticated, StatusCode.PermissionDenied,
        StatusCode.InvalidArgument, StatusCode.AlreadyExists, StatusCode.FailedPrecondition,
        StatusCode.Aborted, StatusCode.Internal, StatusCode.Unavailable,
        StatusCode.DeadlineExceeded, StatusCode.Cancelled,
    };

    [Theory]
    [MemberData(nameof(Codes))]
    public async Task ReadErrorCodePreservedAndTyped(StatusCode code)
    {
        var servicer = new SimulatedPaymentService { ReadErrorCode = code };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.GetClienteAsync(new GetClienteRequest { Id = 1 }));
        Assert.Equal(code, ex.GrpcCode);
        Assert.True(TupagoErrorPredicates.IsTupagoError(ex));
    }

    [Fact]
    public async Task AbortedWithMarkerIsPendingReconciliation()
    {
        var servicer = new SimulatedPaymentService { PendingReconciliationOnCreate = true };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 10, IdempotencyKey = "k1" }));
        Assert.Equal(StatusCode.Aborted, ex.GrpcCode);
        Assert.True(TupagoErrorPredicates.IsPendingReconciliation(ex));
    }

    [Fact]
    public async Task PlainAbortedIsNotPendingReconciliation()
    {
        var servicer = new SimulatedPaymentService { CreateErrorCode = StatusCode.Aborted };
        await using var server = await PaymentTestServer.StartAsync(servicer, useTls: false);
        await using var client = ClientFactory.ForAddress(server.Address);

        var ex = await Assert.ThrowsAnyAsync<TupagoException>(() =>
            client.CreateQrAsync(new CreateQrRequest { IdCuenta = 1, Monto = 10, IdempotencyKey = "k1" }));
        Assert.Equal(StatusCode.Aborted, ex.GrpcCode);
        Assert.False(TupagoErrorPredicates.IsPendingReconciliation(ex));
    }

    public static IEnumerable<object[]> Codes() => _codes.Select(c => new object[] { c });
}
