using Grpc.Core;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Retry;

namespace TuPago3.Sdk.Client;

/// <summary>
/// Maps gRPC / transport failures into the typed TUPAGO error model, preserving status codes and
/// the two financial invariants (UnknownOutcome, PendingReconciliation).
/// </summary>
internal static class ErrorMapping
{
    public static Exception MapRead(Exception ex, string operation)
    {
        if (ex is TupagoException) return ex;
        var (code, msg) = Unwrap(ex);
        if (code == StatusCode.Aborted && msg.Contains(TupagoMarkers.PendingReconciliation, StringComparison.OrdinalIgnoreCase))
            return new TupagoPendingReconciliationException(operation, msg);
        return new TupagoException(operation, code, msg, RetryPolicy.IsRetryableRead(code));
    }

    public static Exception MapCreate(Exception ex, string operation, bool requestSent, string idempotencyKey = "")
    {
        if (ex is TupagoException) return ex;
        var (code, msg) = Unwrap(ex);
        if (code == StatusCode.Aborted && msg.Contains(TupagoMarkers.PendingReconciliation, StringComparison.OrdinalIgnoreCase))
            return new TupagoPendingReconciliationException(operation, msg);
        if (IsDefiniteCreateOutcome(code))
            return new TupagoException(operation, code, msg, false, requestSent);
        if (requestSent)
            return new TupagoUnknownOutcomeException(operation, code, msg, idempotencyKey);
        return new TupagoException(operation, code, "request not sent: " + msg, false, false);
    }

    private static bool IsDefiniteCreateOutcome(StatusCode code) => code switch
    {
        StatusCode.NotFound => true,
        StatusCode.InvalidArgument => true,
        StatusCode.AlreadyExists => true,
        StatusCode.PermissionDenied => true,
        StatusCode.Unauthenticated => true,
        StatusCode.FailedPrecondition => true,
        StatusCode.OutOfRange => true,
        StatusCode.Cancelled => true,
        _ => false
    };

    private static (StatusCode code, string msg) Unwrap(Exception ex)
    {
        if (ex is RpcException rpc)
            return (rpc.StatusCode, rpc.Status.Detail);
        if (ex is OperationCanceledException)
            return (StatusCode.Cancelled, "operation cancelled");
        if (ex is TaskCanceledException)
            return (StatusCode.DeadlineExceeded, "deadline exceeded");
        if (ex is HttpRequestException)
            return (StatusCode.Unavailable, "transport error: " + ex.Message);
        return (StatusCode.Unknown, ex.Message);
    }
}
