using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Errors;
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
        _options = options ?? throw new TupagoConfigException("Options", "options required");
        _credentials = options.Credentials;

        var scheme = options.UseTls ? "https" : "http";
        var baseAddress = $"{scheme}://{options.Target}";

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp2Connections = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = options.DefaultDeadline > TimeSpan.Zero ? options.DefaultDeadline : Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ClientePerfil> GetClienteAsync(GetClienteRequest request, CancellationToken cancellationToken)
    {
        // C5: GET /api/clientes/{id} + catalog composition required by program law.
        var json = await GetAsync($"/api/clientes/{request.Id}", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var clave = root.TryGetProperty("claveInstitucion", out var c) ? c.GetString() ?? "" : "";
        var perfil = new ClientePerfil
        {
            Cliente = new Cliente
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetInt64() : request.Id,
                Nombre = root.TryGetProperty("nombre", out var n) ? n.GetString() ?? "" : "",
                ClaveInstitucion = clave,
                Key = root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
            }
        };
        // Compose catalog if clave is present.
        if (!string.IsNullOrEmpty(clave))
        {
            try
            {
                var catJson = await GetAsync($"/api/catalogo-institucions/clave/{Uri.EscapeDataString(clave)}", cancellationToken).ConfigureAwait(false);
                using var catDoc = JsonDocument.Parse(catJson);
                var catRoot = catDoc.RootElement;
                perfil.CatalogoInstitucion = new CatalogoInstitucion
                {
                    Id = catRoot.TryGetProperty("id", out var cid) ? cid.GetInt64() : 0,
                    ClaveInstitucion = catRoot.TryGetProperty("claveInstitucion", out var cc) ? cc.GetString() ?? clave : clave,
                    NombreCorto = catRoot.TryGetProperty("nombreCorto", out var cn) ? cn.GetString() ?? "" : "",
                };
            }
            catch { /* catalog composition is best-effort; caller can query separately */ }
        }
        return perfil;
    }

    public async Task<CatalogoInstitucion> GetCatalogoAsync(GetCatalogoRequest request, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"/api/catalogo-institucions/clave/{Uri.EscapeDataString(request.ClaveInstitucion)}", cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new CatalogoInstitucion
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
            ClaveInstitucion = root.TryGetProperty("claveInstitucion", out var c) ? c.GetString() ?? request.ClaveInstitucion : request.ClaveInstitucion,
            NombreCorto = root.TryGetProperty("nombreCorto", out var n) ? n.GetString() ?? "" : "",
        };
    }

    public async Task<JsonResponse> GetQrStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        // C3 ByCuenta: POST /api/payments/estatusmsjqr
        var body = new { idc = request.Idc, idCuenta = request.IdCuenta };
        var json = await PostAsync("/api/payments/estatusmsjqr", body, null, cancellationToken).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    public async Task<JsonResponse> GetTelefonicoStatusAsync(GetStatusRequest request, CancellationToken cancellationToken)
    {
        // C4 ByCuenta: POST /api/payments/estatusmsjcobrotelefonico
        // Body uses idMsjdeCobro (NOT idc) per backend3 DTO.
        var body = new { idCuenta = request.IdCuenta, idMsjdeCobro = request.Idc };
        var json = await PostAsync("/api/payments/estatusmsjcobrotelefonico", body, null, cancellationToken).ConfigureAwait(false);
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
        var json = await PostAsync("/api/payments/msjpagoqrusuario", body, request.IdempotencyKey, cancellationToken, isCreate: true).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("mensaje", out var mensaje))
            return new JsonResponse { Json = mensaje.GetRawText() };
        return new JsonResponse { Json = json };
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
        var json = await PostAsync("/api/payments/msjpagotelefonicousuario", body, request.IdempotencyKey, cancellationToken, isCreate: true).ConfigureAwait(false);
        return new JsonResponse { Json = json };
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        await AddAuthAsync(req, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ReadResponseAsync(resp, isCreate: false).ConfigureAwait(false);
    }

    private async Task<string> PostAsync(string path, object body, string? idempotencyKey, CancellationToken ct, bool isCreate = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        await AddAuthAsync(req, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ReadResponseAsync(resp, isCreate).ConfigureAwait(false);
    }

    private async Task AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_credentials is null) return;
        var token = await _credentials.GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new TupagoConfigException("Credentials", "token provider returned a blank token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage resp, bool isCreate = false)
    {
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        // Handle 202 PendingReconciliation BEFORE treating as success.
        if (status == 202)
            throw new TupagoException("REST", Grpc.Core.StatusCode.Aborted, $"HTTP 202 PendingReconciliation: {body}", false);
        // CREATE: 502/503/504 after dispatch = UnknownOutcome (not retryable).
        if (isCreate && status is 502 or 503 or 504)
            throw new TupagoUnknownOutcomeException("REST", $"HTTP {status} after create dispatch: {body}");
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
