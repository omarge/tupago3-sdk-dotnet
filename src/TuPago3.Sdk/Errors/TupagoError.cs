using Grpc.Core;

namespace TuPago3.Sdk.Errors;

/// <summary>
/// Backend signal (carried on an ABORTED status) that the operation is queued/duplicated
/// and must be reconciled. Never auto-retried.
/// </summary>
public static class TupagoMarkers
{
    public const string PendingReconciliation = "TUPAGO_PENDING_RECONCILIATION";
}

/// <summary>
/// Base typed error for every SDK failure. Preserves the originating gRPC status code and
/// a safe (non-secret) description. Inspectable via <see cref="IsTupagoError"/>,
/// <see cref="IsUnknownOutcome"/>, <see cref="IsPendingReconciliation"/> and <see cref="HasStatusCode"/>.
/// </summary>
public class TupagoException : Exception
{
    public TupagoException(string operation, StatusCode grpcCode, string safeMessage, bool retryable)
        : base(Format(operation, grpcCode, safeMessage, retryable, null, false))
    {
        Operation = operation;
        GrpcCode = grpcCode;
        SafeMessage = safeMessage;
        Retryable = retryable;
    }

    internal TupagoException(string operation, StatusCode grpcCode, string safeMessage, bool retryable, bool? sent)
        : base(Format(operation, grpcCode, safeMessage, retryable, sent, sent == false))
    {
        Operation = operation;
        GrpcCode = grpcCode;
        SafeMessage = safeMessage;
        Retryable = retryable;
        Sent = sent;
    }

    public string Operation { get; }
    public StatusCode GrpcCode { get; }
    public string SafeMessage { get; }
    public bool Retryable { get; }

    /// <summary>
    /// Meaningful only when non-null: true means the request bytes were placed on the wire
    /// (indeterminate result); false means the request provably did not reach the server.
    /// Null for read calls (we never assert a send state we did not measure).
    /// </summary>
    public bool? Sent { get; }

    private static string Format(string operation, StatusCode code, string msg, bool retryable, bool? sent, bool notSent)
    {
        var qual = notSent ? " [request-not-sent]" : "";
        return $"tupago: {operation}: {code}: {msg}{qual} (retryable={retryable})";
    }
}

/// <summary>
/// A financial Create whose remote result is indeterminate. MUST NOT be retried blindly and
/// requires reconciliation. Carries the operation, idempotency key, gRPC code and a safe description.
/// </summary>
public sealed class TupagoUnknownOutcomeException : TupagoException
{
    public TupagoUnknownOutcomeException(string operation, StatusCode grpcCode, string safeMessage, string idempotencyKey)
        : base(operation, grpcCode, safeMessage, false, true)
    {
        IdempotencyKey = idempotencyKey;
        Reconcile = true;
    }

    public string IdempotencyKey { get; }
    public bool Reconcile { get; }
}

/// <summary>
/// The backend answered ABORTED with the TUPAGO_PENDING_RECONCILIATION marker. Non-transient and
/// machine-detectable. Never auto-retried.
/// </summary>
public sealed class TupagoPendingReconciliationException : TupagoException
{
    public TupagoPendingReconciliationException(string operation, string safeMessage)
        : base(operation, StatusCode.Aborted, safeMessage, false, true)
    {
    }
}

/// <summary>
/// Invalid client configuration (fail-closed before any network use).
/// </summary>
public sealed class TupagoConfigException : Exception
{
    public TupagoConfigException(string field, string message)
        : base(field == "" ? $"tupago: config: {message}" : $"tupago: config.{field}: {message}")
    {
        Field = field;
    }

    public string Field { get; }
}

/// <summary>
/// Predicates for machine detection of SDK failures.
/// </summary>
public static class TupagoErrorPredicates
{
    public static bool IsTupagoError(Exception? ex) => ex is TupagoException;

    public static bool IsUnknownOutcome(Exception? ex) => ex is TupagoUnknownOutcomeException;

    public static bool IsPendingReconciliation(Exception? ex) => ex is TupagoPendingReconciliationException;

    public static bool IsRequestNotSent(Exception? ex) => ex is TupagoException { Sent: false };

    public static bool HasStatusCode(Exception? ex, StatusCode code)
    {
        if (ex is TupagoException te) return te.GrpcCode == code;
        return StatusCodeHelper.FromException(ex) == code;
    }
}

internal static class StatusCodeHelper
{
    public static StatusCode FromException(Exception? ex)
    {
        if (ex is TupagoException te) return te.GrpcCode;
        if (ex is RpcException rpc) return rpc.StatusCode;
        return StatusCode.Unknown;
    }
}
