using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Generated.V1;
using TuPago3.Sdk.Models;
using TuPago3.Sdk.Retry;
using TuPago3.Sdk.Security;

namespace TuPago3.Sdk.Client;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Idiomatic, thread-safe, reusable gRPC client for the TuPago3 PaymentService.
/// <list type="bullet">
///   <item>Async Task-based methods with <see cref="CancellationToken"/> and per-call deadlines.</item>
///   <item>Dynamic JWT injection via <see cref="ITokenProvider"/> (never stored on the channel).</item>
///   <item>Fail-closed TLS: remote targets require verified TLS; private CA supported.</item>
///   <item>Bounded read retry; Create RPCs are EXACTLY ONE attempt (no blind retry).</item>
///   <item>Typed errors: UnknownOutcome / PendingReconciliation are never auto-retried.</item>
/// </list>
/// </summary>
public sealed class TupagoPaymentClient : IAsyncDisposable, IDisposable
{
    private readonly TupagoClientOptions _options;
    private readonly GrpcChannel _channel;
    private readonly CallInvoker _invoker;
    private readonly ILogger? _logger;
    private int _disposed;

    public TupagoPaymentClient(TupagoClientOptions options)
    {
        _options = (options ?? throw new TupagoConfigException("Options", "options required")).Validate();
        _logger = _options.LoggerFactory?.CreateLogger<TupagoPaymentClient>();

        var scheme = _options.UseTls ? "https" : "http";
        var address = $"{scheme}://{_options.Target}";

        var socketsHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            SslOptions = _options.UseTls
                ? TlsPolicy.BuildSslOptions(_options.CaCertificatePem, _options.ServerName)
                : null!,
            // NOTE: no ServerCertificateCustomValidationCallback that returns true is ever set.
        };

        var handler = new SendTrackingHandler { InnerHandler = socketsHandler };
        var channelOpts = new GrpcChannelOptions
        {
            HttpHandler = handler,
            LoggerFactory = _options.LoggerFactory,
            MaxReceiveMessageSize = 16 * 1024 * 1024,
            MaxSendMessageSize = 16 * 1024 * 1024,
            DisposeHttpClient = true,
        };

        _channel = GrpcChannel.ForAddress(address, channelOpts);
        var invoker = _channel.CreateCallInvoker();
        if (_options.Credentials is not null)
            invoker = invoker.Intercept(new BearerTokenInterceptor(_options.Credentials));
        _invoker = invoker;
    }

    private PaymentService.PaymentServiceClient Raw => new(_invoker);

    // ---- Reads (bounded retry) --------------------------------------------

    public Task<ClientePerfil> GetClienteAsync(GetClienteRequest request, CancellationToken cancellationToken = default)
        => ReadAsync<ClientePerfil>("GetCliente", cancellationToken, ct => Raw.GetClienteAsync(Validate(request), deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);

    public Task<CatalogoInstitucion> GetCatalogoAsync(GetCatalogoRequest request, CancellationToken cancellationToken = default)
        => ReadAsync<CatalogoInstitucion>("GetCatalogo", cancellationToken, ct => Raw.GetCatalogoAsync(Validate(request), deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);

    public Task<JsonResponse> GetQrStatusAsync(GetStatusRequest request, CancellationToken cancellationToken = default)
        => ReadAsync<JsonResponse>("GetQrStatus", cancellationToken, ct => Raw.GetQrStatusAsync(Validate(request), deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);

    public Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken = default)
        => ReadAsync<JsonResponse>("GetTelefonicoStatus", cancellationToken, ct => Raw.GetTelefonicoStatusAsync(Validate(request), deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);

    // ---- Creates (EXACTLY ONE attempt, never retried) ---------------------

    public Task<JsonResponse> CreateQrAsync(CreateQrRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCreate(request);
        return CreateOnceAsync<JsonResponse>("CreateQr", request.IdempotencyKey, cancellationToken,
            ct => Raw.CreateQrAsync(request, deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);
    }

    public Task<JsonResponse> CreateTelefonicoAsync(CreateTelefonicoRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCreate(request);
        return CreateOnceAsync<JsonResponse>("CreateTelefonico", request.IdempotencyKey, cancellationToken,
            ct => Raw.CreateTelefonicoAsync(request, deadline: Deadline(ct), cancellationToken: ct).ResponseAsync);
    }

    // ---- Internals --------------------------------------------------------

    private DateTime? Deadline(CancellationToken ct)
        => _options.DefaultDeadline > TimeSpan.Zero ? DateTime.UtcNow + _options.DefaultDeadline : null;

    private async Task<T> ReadAsync<T>(string op, CancellationToken external, Func<CancellationToken, Task<T>> call)
    {
        try
        {
            return await _options.ReadRetryPolicy.DoAsync(call, external).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ErrorMapping.MapRead(ex, op);
        }
    }

    private async Task<T> CreateOnceAsync<T>(string op, string idempotencyKey, CancellationToken external, Func<CancellationToken, Task<T>> call)
    {
        var tracker = new SendTracker();
        SendTracker.Current.Value = tracker;
        try
        {
            var result = await call(external).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            throw ErrorMapping.MapCreate(ex, op, tracker.Dispatched, idempotencyKey);
        }
        finally
        {
            SendTracker.Current.Value = null;
        }
    }

    private static TRequest Validate<TRequest>(TRequest request)
    {
        if (request is null) throw new TupagoConfigException("Request", "request must not be null");
        return request;
    }

    private static void ValidateCreate(CreateQrRequest req)
    {
        if (req is null) throw new TupagoConfigException("Request", "request must not be null");
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new TupagoConfigException("IdempotencyKey", "idempotency_key is required and preserved verbatim");
        Money.Validate(req.Monto);
    }

    private static void ValidateCreate(CreateTelefonicoRequest req)
    {
        if (req is null) throw new TupagoConfigException("Request", "request must not be null");
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new TupagoConfigException("IdempotencyKey", "idempotency_key is required and preserved verbatim");
        Money.Validate(req.Monto);
    }

    // ---- DI extension -----------------------------------------------------

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Dispose();
            _options.LoggerFactory?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Dispose();
            if (_options.LoggerFactory is not null)
                await Cast(_options.LoggerFactory).ConfigureAwait(false);
        }
    }

    private static async ValueTask Cast(ILoggerFactory factory)
    {
        if (factory is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else factory.Dispose();
    }
}

/// <summary>
/// Optional DI integration: registers <see cref="TupagoPaymentClient"/> as a singleton.
/// Does not force ASP.NET Core as a dependency.
/// </summary>
public static class TupagoServiceCollectionExtensions
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddTuPagoClient(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        TupagoClientOptions options)
    {
        services.AddSingleton(new TupagoPaymentClient(options));
        return services;
    }
}
