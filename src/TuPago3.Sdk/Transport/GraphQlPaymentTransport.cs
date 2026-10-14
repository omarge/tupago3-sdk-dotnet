using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
using TuPago3.Sdk.Security;
using TuPago3.Sdk.Generated.V1;

namespace TuPago3.Sdk.Transport;

/// <summary>
/// GraphQL transport implementing the six canonical capabilities against the verified
/// backend3 GraphQL controller (anchor bbf06c02).
/// Uses ByCuenta queries for C3/C4. Creates use single-attempt / no-blind-retry.
/// Errors carry extensions.code (IDEMPOTENCY_CONFLICT, PAYMENT_PENDING_RECONCILIATION, etc.).
/// </summary>
public sealed class GraphQlPaymentTransport : IPaymentTransport
{
    private readonly HttpClient _http;
    private readonly ITokenProvider? _credentials;

    public GraphQlPaymentTransport(TupagoClientOptions options)
    {
        if (options is null) throw new TupagoConfigException("Options", "options required");
        _credentials = options.Credentials;

        var scheme = options.UseTls ? "https" : "http";
        var baseAddress = $"{scheme}://{options.Target}";

        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (options.UseTls)
            handler.SslOptions = TlsPolicy.BuildSslOptions(options.CaCertificatePem, options.ServerName);
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = options.DefaultDeadline > TimeSpan.Zero ? options.DefaultDeadline : Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ClientePerfil> GetClienteAsync(GetClienteRequest request, CancellationToken cancellationToken)
    {
        var query = "query($id: ID!) { cliente(id: $id) { cliente { id nombre claveInstitucion key } catalogoInstitucion { id claveInstitucion nombreCorto } } }";
        var vars = new { id = request.Id.ToString() };
        var json = await ExecuteAsync(query, vars, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data").GetProperty("cliente");
        var cliente = data.GetProperty("cliente");
        var catalogo = data.GetProperty("catalogoInstitucion");
        return new ClientePerfil
        {
            Cliente = new Cliente
            {
                Id = cliente.GetProperty("id").GetInt64(),
                Nombre = cliente.GetProperty("nombre").GetString() ?? "",
                ClaveInstitucion = cliente.GetProperty("claveInstitucion").GetString() ?? "",
                Key = cliente.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
            },
            CatalogoInstitucion = new CatalogoInstitucion
            {
                Id = catalogo.GetProperty("id").GetInt64(),
                ClaveInstitucion = catalogo.GetProperty("claveInstitucion").GetString() ?? "",
                NombreCorto = catalogo.GetProperty("nombreCorto").GetString() ?? "",
            }
        };
    }

    public async Task<CatalogoInstitucion> GetCatalogoAsync(GetCatalogoRequest request, CancellationToken cancellationToken)
    {
        var query = "query($clave: String!) { catalogoInstitucion(claveInstitucion: $clave) { id claveInstitucion nombreCorto } }";
        var vars = new { clave = request.ClaveInstitucion };
        var json = await ExecuteAsync(query, vars, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data").GetProperty("catalogoInstitucion");
        return new CatalogoInstitucion
        {
            Id = data.GetProperty("id").GetInt64(),
            ClaveInstitucion = data.GetProperty("claveInstitucion").GetString() ?? "",
            NombreCorto = data.GetProperty("nombreCorto").GetString() ?? "",
        };
    }

    public async Task<JsonResponse> GetQrStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        // ByCuenta verified in backend3 GraphQL controller.
        var query = "query($idc: String!, $idCuenta: ID!) { pagoQrEstatusByCuenta(idc: $idc, idCuenta: $idCuenta) }";
        var vars = new { idc = request.Idc, idCuenta = request.IdCuenta.ToString() };
        var json = await ExecuteAsync(query, vars, cancellationToken).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        var query = "query($idc: String!, $idCuenta: ID!) { pagoTelefonicoEstatusByCuenta(idc: $idc, idCuenta: $idCuenta) }";
        var vars = new { idc = request.Idc, idCuenta = request.IdCuenta.ToString() };
        var json = await ExecuteAsync(query, vars, cancellationToken).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> CreateQrAsync(CreateQrRequest request, CancellationToken cancellationToken)
    {
        var mutation = "mutation($input: PagoQrInput!, $key: String) { createPagoQr(input: $input, idempotencyKey: $key) }";
        var vars = new
        {
            input = new
            {
                idCuenta = request.IdCuenta.ToString(),
                monto = request.Monto,
                concepto = request.Concepto,
                referenciaNumerica = request.ReferenciaNumerica,
                nombreCliente = request.NombreCliente,
                email = request.Email,
                tipoPago = request.TipoPago,
                vigencia = request.Vigencia,
            },
            key = request.IdempotencyKey,
        };
        var json = await ExecuteAsync(mutation, vars, cancellationToken).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> CreateTelefonicoAsync(CreateTelefonicoRequest request, CancellationToken cancellationToken)
    {
        var mutation = "mutation($input: PagoTelefonicoInput!, $key: String) { createPagoTelefonico(input: $input, idempotencyKey: $key) }";
        var vars = new
        {
            input = new
            {
                key = request.Key,
                monto = request.Monto,
                concepto = request.Concepto,
                referenciaNumerica = request.ReferenciaNumerica,
                numeroCelular = request.NumeroCelular,
                vigencia = request.Vigencia,
            },
            key = request.IdempotencyKey,
        };
        var json = await ExecuteAsync(mutation, vars, cancellationToken).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    private async Task<string> ExecuteAsync(string query, object variables, CancellationToken ct)
    {
        var payload = new { query, variables };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        if (_credentials is not null)
        {
            var token = await _credentials.GetTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                throw new TupagoConfigException("Credentials", "token provider returned a blank token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new TupagoException("GraphQL", Grpc.Core.StatusCode.Unknown, $"HTTP {(int)resp.StatusCode}: {body}", false);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var code = first.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c)
                ? c.GetString()
                : "UNKNOWN";
            var grpcCode = code switch
            {
                "IDEMPOTENCY_CONFLICT" => Grpc.Core.StatusCode.AlreadyExists,
                "PAYMENT_PENDING_RECONCILIATION" => Grpc.Core.StatusCode.Aborted,
                "PAYMENT_LIMIT" => Grpc.Core.StatusCode.InvalidArgument,
                _ => Grpc.Core.StatusCode.Unknown,
            };
            throw new TupagoException("GraphQL", grpcCode, code ?? "error", false);
        }

        return body;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
