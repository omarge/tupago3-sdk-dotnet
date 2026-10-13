using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Retry;
using TuPago3.Sdk.Security;
using TuPago3.Sdk.Transport;

namespace TuPago3.Sdk.Client;

/// <summary>
/// Immutable, validated configuration for <see cref="TupagoPaymentClient"/>.
/// </summary>
public sealed class TupagoClientOptions
{
    /// <summary>
    /// Target host:port, e.g. "127.0.0.1:9090". Plaintext is only permitted for loopback.
    /// </summary>
    public string Target { get; init; } = "";

    /// <summary>
    /// Preferred transport. Currently only Grpc is fully implemented.
    /// </summary>
    public TransportKind Transport { get; init; } = TransportKind.Grpc;

    /// <summary>
    /// When true, connect over TLS (required for any non-loopback target). Default: false.
    /// </summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// PEM-encoded trusted CA(s) for private PKI. Validated; never bypasses verification.
    /// </summary>
    public string? CaCertificatePem { get; init; }

    /// <summary>
    /// Expected TLS server name (matched against the certificate SAN). Does not disable verification.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Dynamic token provider. Required by default (fail-closed). Set AllowUnauthenticatedLab=true
    /// only for explicit isolated lab tests that intentionally omit credentials.
    /// </summary>
    public ITokenProvider? Credentials { get; init; }

    /// <summary>
    /// Explicit opt-in for lab/test scenarios that intentionally run without credentials.
    /// Production callers must leave this false.
    /// </summary>
    public bool AllowUnauthenticatedLab { get; init; }

    /// <summary>
    /// Per-call deadline applied to every RPC unless overridden. Default 30s.
    /// Note: retry policy MaxElapsed interacts with this; a total public-call budget is preferred long-term.
    /// </summary>
    public TimeSpan DefaultDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Read retry policy. Create RPCs always use <see cref="RetryPolicy.NoRetry"/> regardless.
    /// </summary>
    public RetryPolicy ReadRetryPolicy { get; init; } = RetryPolicy.BoundedReadBackoff();

    /// <summary>
    /// Optional logger factory (no secrets are ever logged). The SDK does not take ownership;
    /// the caller is responsible for disposing it.
    /// </summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

    public TupagoClientOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(Target))
            throw new TupagoConfigException("Target", "target is required");
        TlsPolicy.RequireTlsForTarget(Target, UseTls);
        if (Credentials is null && !AllowUnauthenticatedLab)
            throw new TupagoConfigException("Credentials", "credentials are required; set AllowUnauthenticatedLab only for explicit lab tests");
        if (UseTls && string.IsNullOrWhiteSpace(ServerName))
        {
            // Derive a server name from the target host when not supplied.
            var host = Target.Contains(':') ? Target[..Target.LastIndexOf(':')] : Target;
            ServerName = host;
        }
        return this;
    }
}
