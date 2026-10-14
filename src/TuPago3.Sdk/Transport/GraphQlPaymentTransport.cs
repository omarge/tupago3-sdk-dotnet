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
        options = (options ?? throw new TupagoConfigException("Options", "options required")).Validate();
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
        var data = await ExecuteFieldAsync(
            "GetCliente", "cliente", query, vars, cancellationToken, isCreate: false).ConfigureAwait(false);
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
        var data = await ExecuteFieldAsync(
            "GetCatalogo", "catalogoInstitucion", query, vars, cancellationToken, isCreate: false).ConfigureAwait(false);
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
        var data = await ExecuteFieldAsync(
            "GetQrStatus", "pagoQrEstatusByCuenta", query, vars, cancellationToken, isCreate: false).ConfigureAwait(false);
        return new JsonResponse { Json = data.GetRawText() };
    }

    public async Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        var query = "query($idc: String!, $idCuenta: ID!) { pagoTelefonicoEstatusByCuenta(idc: $idc, idCuenta: $idCuenta) }";
        var vars = new { idc = request.Idc, idCuenta = request.IdCuenta.ToString() };
        var data = await ExecuteFieldAsync(
            "GetTelefonicoStatus", "pagoTelefonicoEstatusByCuenta", query, vars, cancellationToken, isCreate: false).ConfigureAwait(false);
        return new JsonResponse { Json = data.GetRawText() };
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
        var data = await ExecuteFieldAsync(
            "CreateQr", "createPagoQr", mutation, vars, cancellationToken,
            isCreate: true, idempotencyKey: request.IdempotencyKey).ConfigureAwait(false);
        return new JsonResponse { Json = data.GetRawText() };
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
        var data = await ExecuteFieldAsync(
            "CreateTelefonico", "createPagoTelefonico", mutation, vars, cancellationToken,
            isCreate: true, idempotencyKey: request.IdempotencyKey).ConfigureAwait(false);
        return new JsonResponse { Json = data.GetRawText() };
    }

    private async Task<JsonElement> ExecuteFieldAsync(
        string operation,
        string field,
        string query,
        object variables,
        CancellationToken ct,
        bool isCreate,
        string idempotencyKey = "")
    {
        ct.ThrowIfCancellationRequested();
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

        ct.ThrowIfCancellationRequested();
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (isCreate)
        {
            throw new TupagoUnknownOutcomeException(
                operation, Grpc.Core.StatusCode.Unknown,
                $"transport failure after GraphQL create dispatch: {ex.GetType().Name}", idempotencyKey);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (isCreate && ((status >= 300 && status < 400) || status >= 500))
                throw new TupagoUnknownOutcomeException(
                    operation, Grpc.Core.StatusCode.Unknown,
                    $"HTTP {status} after GraphQL create dispatch", idempotencyKey);

            if (!resp.IsSuccessStatusCode)
                throw MapHttpError(operation, status, body);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                if (isCreate)
                    throw new TupagoUnknownOutcomeException(
                        operation, Grpc.Core.StatusCode.Unknown,
                        "malformed GraphQL create response after dispatch", idempotencyKey);
                throw new TupagoException(operation, Grpc.Core.StatusCode.Internal,
                    "malformed GraphQL response", false);
            }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                    throw MapGraphQlError(operation, errors[0], isCreate, idempotencyKey);

                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || !data.TryGetProperty(field, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    if (isCreate)
                        throw new TupagoUnknownOutcomeException(
                            operation, Grpc.Core.StatusCode.Unknown,
                            $"GraphQL create response missing data.{field} after dispatch", idempotencyKey);
                    throw new TupagoException(operation, Grpc.Core.StatusCode.Internal,
                        $"GraphQL response missing data.{field}", false);
                }
                return value.Clone();
            }
        }
    }

    private static TupagoException MapHttpError(string operation, int status, string body)
    {
        var code = status switch
        {
            400 => Grpc.Core.StatusCode.InvalidArgument,
            401 => Grpc.Core.StatusCode.Unauthenticated,
            403 => Grpc.Core.StatusCode.PermissionDenied,
            404 => Grpc.Core.StatusCode.NotFound,
            409 => Grpc.Core.StatusCode.AlreadyExists,
            502 or 503 or 504 => Grpc.Core.StatusCode.Unavailable,
            >= 500 => Grpc.Core.StatusCode.Internal,
            _ => Grpc.Core.StatusCode.Unknown,
        };
        return new TupagoException(operation, code, $"HTTP {status}: {body}",
            TuPago3.Sdk.Retry.RetryPolicy.IsRetryableRead(code));
    }

    private static TupagoException MapGraphQlError(
        string operation, JsonElement first, bool isCreate, string idempotencyKey)
    {
        var code = first.TryGetProperty("extensions", out var ext)
            && ext.TryGetProperty("code", out var c)
            ? c.GetString() ?? "UNKNOWN"
            : "UNKNOWN";
        var message = first.TryGetProperty("message", out var m)
            ? m.GetString() ?? code
            : code;

        return code switch
        {
            "PAYMENT_PENDING_RECONCILIATION" =>
                new TupagoPendingReconciliationException(operation, message),
            "IDEMPOTENCY_CONFLICT" or "ALREADY_EXISTS" =>
                new TupagoException(operation, Grpc.Core.StatusCode.AlreadyExists, message, false),
            "POLICY_REVISION_CONFLICT" or "FAILED_PRECONDITION" =>
                new TupagoException(operation, Grpc.Core.StatusCode.FailedPrecondition, message, false),
            "PAYMENT_LIMIT" or "INVALID_ARGUMENT" =>
                new TupagoException(operation, Grpc.Core.StatusCode.InvalidArgument, message, false),
            "UNAUTHENTICATED" =>
                new TupagoException(operation, Grpc.Core.StatusCode.Unauthenticated, message, false),
            "PERMISSION_DENIED" =>
                new TupagoException(operation, Grpc.Core.StatusCode.PermissionDenied, message, false),
            "NOT_FOUND" =>
                new TupagoException(operation, Grpc.Core.StatusCode.NotFound, message, false),
            _ when isCreate =>
                new TupagoUnknownOutcomeException(
                    operation, Grpc.Core.StatusCode.Unknown,
                    $"ambiguous GraphQL create error after dispatch: {message}", idempotencyKey),
            _ =>
                new TupagoException(operation, Grpc.Core.StatusCode.Internal,
                    $"unrecognized GraphQL error: {message}", false),
        };
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
