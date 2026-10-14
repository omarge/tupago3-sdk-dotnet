using TuPago3.Sdk.Generated.V1;

namespace TuPago3.Sdk.Transport;

/// <summary>
/// Transport-neutral operations for the six canonical capabilities (C1-C6).
/// Implementations must preserve single-attempt / no-blind-retry semantics for creates,
/// map errors to the public taxonomy, and never invent commercial limits.
/// </summary>
public interface IPaymentTransport : IAsyncDisposable
{
    Task<ClientePerfil> GetClienteAsync(GetClienteRequest request, CancellationToken cancellationToken);
    Task<CatalogoInstitucion> GetCatalogoAsync(GetCatalogoRequest request, CancellationToken cancellationToken);
    Task<JsonResponse> GetQrStatusAsync(GetStatusRequest request, CancellationToken cancellationToken);
    Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken);
    Task<JsonResponse> CreateQrAsync(CreateQrRequest request, CancellationToken cancellationToken);
    Task<JsonResponse> CreateTelefonicoAsync(CreateTelefonicoRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Marker for the preferred transport. gRPC, REST and GraphQL implement the same six
/// backend3 capabilities and must preserve equivalent public semantics.
/// </summary>
public enum TransportKind
{
    Grpc = 0,
    Rest = 1,
    GraphQl = 2
}
