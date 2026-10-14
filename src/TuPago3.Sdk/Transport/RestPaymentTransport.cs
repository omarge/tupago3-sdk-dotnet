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
/// REST transport implementing the six canonical capabilities against the verified
/// backend3 routes (anchor bbf06c02).
/// Creates use single-attempt / no-blind-retry. Responses are normalized to the
/// same public types used by gRPC where practical.
/// </summary>
public sealed class RestPaymentTransport : IPaymentTransport
{
    private readonly HttpClient _http;
    private readonly TupagoClientOptions _options;
    private readonly ITokenProvider? _credentials;

    public RestPaymentTransport(TupagoClientOptions options)
    {
        _options = (options ?? throw new TupagoConfigException("Options", "options required")).Validate();
        _credentials = _options.Credentials;

        var scheme = _options.UseTls ? "https" : "http";
        var baseAddress = $"{scheme}://{_options.Target}";

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true,
        };
        if (_options.UseTls)
            handler.SslOptions = TlsPolicy.BuildSslOptions(_options.CaCertificatePem, _options.ServerName);

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = _options.DefaultDeadline > TimeSpan.Zero ? _options.DefaultDeadline : Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ClientePerfil> GetClienteAsync(GetClienteRequest request, CancellationToken cancellationToken)
    {
        // C5: GET /api/clientes/{id} + catalog composition. Fail-closed on missing required fields.
        var json = await GetAsync($"/api/clientes/{request.Id}", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("nombre", out var nombreEl) || !root.TryGetProperty("claveInstitucion", out var claveEl))
            throw new TupagoException("GetCliente", Grpc.Core.StatusCode.Internal, "malformed cliente response: missing required fields", false);
        var clave = claveEl.GetString() ?? "";
        if (string.IsNullOrEmpty(clave))
            throw new TupagoException("GetCliente", Grpc.Core.StatusCode.Internal, "malformed cliente response: empty claveInstitucion", false);
        var perfil = new ClientePerfil
        {
            Cliente = new Cliente
            {
                Id = idEl.GetInt64(),
                Nombre = nombreEl.GetString() ?? "",
                ClaveInstitucion = clave,
                Key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
            }
        };
        // Compose catalog. Fail-closed if catalog fetch fails.
        var catJson = await GetAsync($"/api/catalogo-institucions/clave/{Uri.EscapeDataString(clave)}", cancellationToken).ConfigureAwait(false);
        using var catDoc = JsonDocument.Parse(catJson);
        var catRoot = catDoc.RootElement;
        if (!catRoot.TryGetProperty("id", out var cidEl) || !catRoot.TryGetProperty("claveInstitucion", out var ccEl) || !catRoot.TryGetProperty("nombreCorto", out var cnEl))
            throw new TupagoException("GetCliente", Grpc.Core.StatusCode.Internal, "malformed catalogo response: missing required fields", false);
        perfil.CatalogoInstitucion = new CatalogoInstitucion
        {
            Id = cidEl.GetInt64(),
            ClaveInstitucion = ccEl.GetString() ?? "",
            NombreCorto = cnEl.GetString() ?? "",
        };
        return perfil;
    }

    public async Task<CatalogoInstitucion> GetCatalogoAsync(GetCatalogoRequest request, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"/api/catalogo-institucions/clave/{Uri.EscapeDataString(request.ClaveInstitucion)}", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl)
            || !root.TryGetProperty("claveInstitucion", out var claveEl)
            || !root.TryGetProperty("nombreCorto", out var nombreEl))
            throw new TupagoException("GetCatalogo", Grpc.Core.StatusCode.Internal,
                "malformed catalogo response: missing required fields", false);
        var clave = claveEl.GetString();
        var nombre = nombreEl.GetString();
        if (idEl.GetInt64() == 0 || string.IsNullOrWhiteSpace(clave) || string.IsNullOrWhiteSpace(nombre))
            throw new TupagoException("GetCatalogo", Grpc.Core.StatusCode.Internal,
                "malformed catalogo response: default/blank required fields", false);
        return new CatalogoInstitucion
        {
            Id = idEl.GetInt64(),
            ClaveInstitucion = clave,
            NombreCorto = nombre,
        };
    }

    public async Task<JsonResponse> GetQrStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        // C3 ByCuenta: POST /api/payments/estatusmsjqr
        var body = new { idc = request.Idc, idCuenta = request.IdCuenta };
        var json = await PostAsync("/api/payments/estatusmsjqr", body, null, cancellationToken, operation: "GetQrStatus").ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        // C4 ByCuenta: POST /api/payments/estatusmsjcobrotelefonico
        // Body uses idMsjdeCobro (NOT idc) per backend3 DTO.
        var body = new { idCuenta = request.IdCuenta, idMsjdeCobro = request.Idc };
        var json = await PostAsync("/api/payments/estatusmsjcobrotelefonico", body, null, cancellationToken, operation: "GetTelefonicoStatus").ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> CreateQrAsync(CreateQrRequest request, CancellationToken cancellationToken)
    {
        // C1: POST /api/payments/msjpagoqrusuario + Idempotency-Key.
        // Response wraps result under "mensaje".
        var body = new
        {
            idCuenta = request.IdCuenta,
            monto = request.Monto,
            concepto = request.Concepto,
            referenciaNumerica = request.ReferenciaNumerica,
            nombreCliente = request.NombreCliente,
            email = request.Email,
            tipoPago = request.TipoPago,
            vigencia = request.Vigencia,
        };
        var json = await PostAsync("/api/payments/msjpagoqrusuario", body, request.IdempotencyKey, cancellationToken,
            isCreate: true, operation: "CreateQr").ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mensaje", out var mensaje)
                || mensaje.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new TupagoUnknownOutcomeException("CreateQr", Grpc.Core.StatusCode.Unknown,
                    "create response missing mensaje after dispatch", request.IdempotencyKey);
            return new JsonResponse { Json = mensaje.GetRawText() };
        }
        catch (JsonException)
        {
            // Malformed 2xx after create dispatch = UnknownOutcome.
            throw new TupagoUnknownOutcomeException("CreateQr", Grpc.Core.StatusCode.Unknown, "malformed create response after dispatch", request.IdempotencyKey);
        }
    }

    public async Task<JsonResponse> CreateTelefonicoAsync(CreateTelefonicoRequest request, CancellationToken cancellationToken)
    {
        // C2 CANONICAL PEER: POST /api/payments/msjpagotelefonicousuario
        // Uses resolveSingleOwnedCliente() — same authority as gRPC/GraphQL.
        // NOT /msjpagotelefonico (that uses requireOwnedClienteByKey).
        var body = new
        {
            key = request.Key,
            monto = request.Monto,
            concepto = request.Concepto,
            referenciaNumerica = request.ReferenciaNumerica,
            numeroCelular = request.NumeroCelular,
            vigencia = request.Vigencia,
        };
        var json = await PostAsync("/api/payments/msjpagotelefonicousuario", body, request.IdempotencyKey, cancellationToken,
            isCreate: true, operation: "CreateTelefonico").ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        await AddAuthAsync(req, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ReadResponseAsync(resp, isCreate: false).ConfigureAwait(false);
    }

    private async Task<string> PostAsync(
        string path, object body, string? idempotencyKey, CancellationToken ct,
        bool isCreate = false, string operation = "REST")
    {
        ct.ThrowIfCancellationRequested();
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        await AddAuthAsync(req, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return await ReadResponseAsync(resp, isCreate, operation, idempotencyKey ?? "").ConfigureAwait(false);
        }
        catch (TupagoException)
        {
            throw;
        }
        catch (Exception ex) when (isCreate)
        {
            throw new TupagoUnknownOutcomeException(
                operation, Grpc.Core.StatusCode.Unknown,
                $"transport failure after create dispatch: {ex.GetType().Name}", idempotencyKey ?? "");
        }
    }

    private async Task AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_credentials is null) return;
        var token = await _credentials.GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new TupagoConfigException("Credentials", "token provider returned a blank token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> ReadResponseAsync(
        HttpResponseMessage resp, bool isCreate = false, string operation = "REST", string idempotencyKey = "")
    {
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        // Handle 202 PendingReconciliation BEFORE treating as generic success.
        if (status == 202)
            throw new TupagoPendingReconciliationException(operation, $"HTTP 202 PendingReconciliation: {body}");
        // A redirect or server-side 5xx after a financial POST is ambiguous. Never auto-follow/resubmit.
        if (isCreate && ((status >= 300 && status < 400) || status >= 500))
            throw new TupagoUnknownOutcomeException(
                operation, Grpc.Core.StatusCode.Unknown, $"HTTP {status} after create dispatch", idempotencyKey);
        if (resp.IsSuccessStatusCode) return body;

        var code = resp.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => Grpc.Core.StatusCode.Unauthenticated,
            System.Net.HttpStatusCode.Forbidden => Grpc.Core.StatusCode.PermissionDenied,
            System.Net.HttpStatusCode.NotFound => Grpc.Core.StatusCode.NotFound,
            System.Net.HttpStatusCode.Conflict => Grpc.Core.StatusCode.AlreadyExists,
            System.Net.HttpStatusCode.BadRequest => Grpc.Core.StatusCode.InvalidArgument,
            _ => Grpc.Core.StatusCode.Unknown,
        };
        throw new TupagoException("REST", code, $"HTTP {status}: {body}", false);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
