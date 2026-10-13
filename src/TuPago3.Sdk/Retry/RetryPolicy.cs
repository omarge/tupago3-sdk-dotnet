using Grpc.Core;
using TuPago3.Sdk.Errors;

namespace TuPago3.Sdk.Retry;

/// <summary>
/// Bounded, read-only retry with backoff. Create RPCs use <see cref="NoRetry"/>: they must never
/// be retried automatically (financial invariant). Only clearly transient read failures are retried.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 1;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(20);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxElapsed { get; init; } = TimeSpan.FromSeconds(1);

    public static RetryPolicy NoRetry() => new() { MaxAttempts = 1 };

    public static RetryPolicy BoundedReadBackoff() => new()
    {
        MaxAttempts = 4,
        BaseDelay = TimeSpan.FromMilliseconds(20),
        MaxDelay = TimeSpan.FromMilliseconds(200),
        MaxElapsed = TimeSpan.FromSeconds(1),
    };

    /// <summary>
    /// Conservative default: only UNAVAILABLE and DEADLINE_EXCEEDED are candidates for bounded retry.
    /// RESOURCE_EXHAUSTED, INTERNAL and UNKNOWN are not blindly retried unless the server provides
    /// explicit safe-retry semantics (e.g. RetryInfo) which are not currently parsed.
    /// </summary>
    public static bool IsRetryableRead(StatusCode code) => code switch
    {
        StatusCode.Unavailable => true,
        StatusCode.DeadlineExceeded => true,
        _ => false
    };

    public async Task<T> DoAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var lastEx = default(Exception);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = Backoff(attempt);
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        if (lastEx is not null) throw lastEx;
                        throw;
                    }
                }
            }

            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            try
            {
                return await op(ct);
            }
            catch (Exception ex) when (ex is RpcException or TupagoException)
            {
                lastEx = ex;
                var code = ex is RpcException rpc ? rpc.StatusCode
                    : ex is TupagoException te ? te.GrpcCode
                    : StatusCode.Unknown;
                if (!IsRetryableRead(code))
                    throw;
                if (MaxElapsed > TimeSpan.Zero && DateTime.UtcNow - start > MaxElapsed)
                    throw;
            }
        }
        if (lastEx is not null) throw lastEx;
        throw new InvalidOperationException("tupago: retry exhausted without result");
    }

    private TimeSpan Backoff(int attempt)
    {
        if (BaseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        var ms = BaseDelay.TotalMilliseconds * (1 << (attempt - 1));
        if (MaxDelay > TimeSpan.Zero && ms > MaxDelay.TotalMilliseconds)
            ms = MaxDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }
}
