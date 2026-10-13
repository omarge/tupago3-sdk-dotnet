# TuPago3.Sdk

Official .NET/C# SDK for the **TuPago3 PaymentService** gRPC API.

- Target: `net10.0`
- Transport-security enforced (TLS required for remote targets; plaintext only for loopback)
- Dynamic JWT injection via `ITokenProvider` (never stored on the channel)
- Bounded read retry; **Create RPCs use single-attempt / no-blind-retry financial create**
- Typed errors: `TupagoUnknownOutcomeException`, `TupagoPendingReconciliationException`

## Quick start

```csharp
using TuPago3.Sdk;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;

var client = new TupagoPaymentClient(new TupagoClientOptions
{
    Target = "127.0.0.1:9090",
    UseTls = false, // loopback dev only
    Credentials = new StaticTokenProvider(token),
});

var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
await client.DisposeAsync();
```

## Financial invariants

`CreateQr` / `CreateTelefonico` issue **at most one** SDK dispatch per public call (single-attempt / no-blind-retry). Transient transport
errors after the request is sent map to `TupagoUnknownOutcomeException` (reconcile with the same idempotency key, do not blind-retry).
`TUPAGO_PENDING_RECONCILIATION` (ABORTED) maps to `TupagoPendingReconciliationException`. The
idempotency key is preserved verbatim and never regenerated.

## License

Proprietary. Internal controlled distribution only.
