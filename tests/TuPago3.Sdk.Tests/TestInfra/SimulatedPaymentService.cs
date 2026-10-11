using Grpc.Core;
using System.Security.Cryptography;
using TuPago3.Sdk.Generated.V1;

namespace TuPago3.Sdk.Tests.TestInfra;

/// <summary>
/// Faithful in-memory gRPC server for the TUPAGO3 PaymentService, used EXCLUSIVELY by the SDK test
/// suite. Mirrors backend3 semantics for the behaviours the SDK must handle: JWT bearer validation
/// (HS512), the stable TUPAGO_PENDING_RECONCILIATION ABORTED condition, idempotency conflict
/// (ALREADY_EXISTS), missing-result (NOT_FOUND) and configurable transport faults
/// (UNAVAILABLE / DEADLINE / slow response to simulate lost response). Counts create invocations so
/// the "exactly one attempt" financial invariant can be verified. NEVER contacts Banxico/PSP.
/// </summary>
public sealed class SimulatedPaymentService : PaymentService.PaymentServiceBase
{
    private readonly object _gate = new();

    public string? LabSecretBase64 { get; set; }

    public int CreateQrCount { get; private set; }
    public int CreateTelefonicoCount { get; private set; }
    public List<string> ReceivedQrKeys { get; } = new();
    public List<string> ReceivedTelefonicoKeys { get; } = new();
    public int GetClienteCount { get; private set; }

    public bool UnavailableOnCreate { get; set; }
    public bool DeadlineOnCreate { get; set; }
    public bool PendingReconciliationOnCreate { get; set; }
    public bool AlreadyExistsOnCreate { get; set; }
    public double SlowCreateSeconds { get; set; }

    public bool UnavailableOnRead { get; set; }
    public bool NotFoundOnGetCliente { get; set; }
    public int UnavailableReadFailuresRemaining { get; set; }

    public Grpc.Core.StatusCode? ReadErrorCode { get; set; }
    public Grpc.Core.StatusCode? CreateErrorCode { get; set; }

    public string LastReceivedIdempotencyKey { get; private set; } = "";

    private void CheckAuth(ServerCallContext context)
    {
        if (LabSecretBase64 is null) return;
        var md = context.RequestHeaders;
        var header = md.GetValue("authorization");
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer "))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing bearer token"));
        var token = header["Bearer ".Length..];
        if (!VerifyHs512(Convert.FromBase64String(LabSecretBase64), token))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
        if (IsExpired(token))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
    }

    private void MaybeFaultCreate()
    {
        if (PendingReconciliationOnCreate)
            throw new RpcException(new Status(StatusCode.Aborted,
                "TUPAGO_PENDING_RECONCILIATION; payment pending reconciliation; do not retry"));
        if (AlreadyExistsOnCreate)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Idempotency conflict"));
        if (UnavailableOnCreate)
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        if (DeadlineOnCreate)
        {
            Thread.Sleep(TimeSpan.FromSeconds(SlowCreateSeconds > 0 ? SlowCreateSeconds : 5));
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Simulated deadline"));
        }
    }

    private void FailUnavailableReadIfConfigured()
    {
        if (UnavailableReadFailuresRemaining > 0)
        {
            UnavailableReadFailuresRemaining--;
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        }
    }

    public override Task<ClientePerfil> GetCliente(GetClienteRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        lock (_gate) GetClienteCount++;
        if (ReadErrorCode.HasValue)
            throw new RpcException(new Status(ReadErrorCode.Value, "injected read error"));
        FailUnavailableReadIfConfigured();
        if (UnavailableOnRead)
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        if (NotFoundOnGetCliente)
            throw new RpcException(new Status(StatusCode.NotFound, "No result available; upstream returned no value"));
        var perfil = new ClientePerfil
        {
            Cliente = new Cliente { Id = request.Id, Nombre = "Cliente Demo", ClaveInstitucion = "012", Key = "KEY1" },
            CatalogoInstitucion = new CatalogoInstitucion { Id = 1, ClaveInstitucion = "012", NombreCorto = "Banco Demo" }
        };
        return Task.FromResult(perfil);
    }

    public override Task<CatalogoInstitucion> GetCatalogo(GetCatalogoRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        if (ReadErrorCode.HasValue)
            throw new RpcException(new Status(ReadErrorCode.Value, "injected read error"));
        FailUnavailableReadIfConfigured();
        if (UnavailableOnRead)
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        return Task.FromResult(new CatalogoInstitucion
        {
            Id = 1,
            ClaveInstitucion = request.ClaveInstitucion,
            NombreCorto = "Banco Demo"
        });
    }

    public override Task<JsonResponse> GetQrStatus(GetStatusRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        if (ReadErrorCode.HasValue)
            throw new RpcException(new Status(ReadErrorCode.Value, "injected read error"));
        FailUnavailableReadIfConfigured();
        if (UnavailableOnRead)
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        return Task.FromResult(new JsonResponse { Json = "{\"status\":\"PENDING\",\"idc\":\"" + request.Idc + "\"}" });
    }

    public override Task<JsonResponse> GetTelefonicoStatus(GetStatusRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        if (ReadErrorCode.HasValue)
            throw new RpcException(new Status(ReadErrorCode.Value, "injected read error"));
        FailUnavailableReadIfConfigured();
        if (UnavailableOnRead)
            throw new RpcException(new Status(StatusCode.Unavailable, "Simulated unavailable"));
        return Task.FromResult(new JsonResponse { Json = "{\"status\":\"PENDING\",\"idc\":\"" + request.Idc + "\"}" });
    }

    public override Task<JsonResponse> CreateQr(CreateQrRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        lock (_gate)
        {
            CreateQrCount++;
            ReceivedQrKeys.Add(request.IdempotencyKey);
            LastReceivedIdempotencyKey = request.IdempotencyKey;
        }
        if (CreateErrorCode.HasValue)
            throw new RpcException(new Status(CreateErrorCode.Value, "injected create error"));
        if (SlowCreateSeconds > 0) Thread.Sleep(TimeSpan.FromSeconds(SlowCreateSeconds));
        MaybeFaultCreate();
        return Task.FromResult(new JsonResponse
        {
            Json = "{\"id\":\"qr-1\",\"monto\":" + request.Monto.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"key\":\"" + request.IdempotencyKey + "\"}"
        });
    }

    public override Task<JsonResponse> CreateTelefonico(CreateTelefonicoRequest request, ServerCallContext context)
    {
        CheckAuth(context);
        lock (_gate)
        {
            CreateTelefonicoCount++;
            ReceivedTelefonicoKeys.Add(request.IdempotencyKey);
            LastReceivedIdempotencyKey = request.IdempotencyKey;
        }
        if (CreateErrorCode.HasValue)
            throw new RpcException(new Status(CreateErrorCode.Value, "injected create error"));
        if (SlowCreateSeconds > 0) Thread.Sleep(TimeSpan.FromSeconds(SlowCreateSeconds));
        MaybeFaultCreate();
        return Task.FromResult(new JsonResponse
        {
            Json = "{\"id\":\"tel-1\",\"monto\":" + request.Monto.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"key\":\"" + request.IdempotencyKey + "\"}"
        });
    }

    // ---- HS512 verification (test-only mirror of backend3 lab interceptor) ----

    internal static bool VerifyHs512(byte[] secret, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        var signingInput = System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        using var hmac = new HMACSHA512(secret);
        var expected = hmac.ComputeHash(signingInput);
        var sig = Base64UrlDecode(parts[2]);
        if (expected.Length != sig.Length) return false;
        var diff = 0;
        for (var i = 0; i < expected.Length; i++)
            diff |= expected[i] ^ sig[i];
        return diff == 0;
    }

    internal static bool IsExpired(string token)
    {
        try
        {
            var payload = token.Split('.')[1];
            var pad = payload.Length % 4 == 0 ? "" : new string('=', 4 - (payload.Length % 4));
            var json = System.Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/') + pad);
            var doc = System.Text.Encoding.UTF8.GetString(json);
            var idx = doc.IndexOf("\"exp\"");
            if (idx < 0) return false;
            var start = doc.IndexOf(':', idx) + 1;
            var end = start;
            while (end < doc.Length && char.IsDigit(doc[end])) end++;
            if (long.TryParse(doc[start..end], out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime < DateTime.UtcNow;
        }
        catch { return true; }
        return false;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var pad = s.Length % 4 == 0 ? "" : new string('=', 4 - (s.Length % 4));
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + pad);
    }
}
