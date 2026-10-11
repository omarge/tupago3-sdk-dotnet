using Grpc.Core;
using Grpc.Core.Interceptors;

namespace TuPago3.Sdk.Authentication;

/// <summary>
/// Per-RPC interceptor that injects `Authorization: Bearer &lt;token&gt;` from an
/// <see cref="ITokenProvider"/>. The token is never written to any log. When no provider is set
/// (loopback dev without auth) the call proceeds without the header.
/// </summary>
public sealed class BearerTokenInterceptor : Interceptor
{
    private readonly ITokenProvider? _provider;

    public BearerTokenInterceptor(ITokenProvider? provider) => _provider = provider;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (_provider is null)
            return continuation(request, context);

        // Token is fetched synchronously on the call path; providers are expected to be cheap
        // (cached) so this does not block the async pipeline meaningfully.
        var token = _provider.GetTokenAsync(context.Options.CancellationToken).GetAwaiter().GetResult();

        var headers = context.Options.Headers;
        if (headers is null)
        {
            headers = new Metadata { { "authorization", "Bearer " + token } };
        }
        else if (!headers.Any(h => h.Key == "authorization"))
        {
            headers.Add("authorization", "Bearer " + token);
        }

        var opts = context.Options.WithHeaders(headers);
        var ctx = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, opts);
        return continuation(request, ctx);
    }
}
