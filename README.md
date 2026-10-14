# TuPago3.Sdk

SDK oficial .NET/C# para el **TuPago3 PaymentService**.

- Target: `net10.0`
- Transportes: **gRPC** (default), **REST** y **GraphQL**
- Seguridad de transporte: TLS obligatorio para destinos remotos; plaintext solo para identidades locales explícitas
- Inyección dinámica de JWT vía `ITokenProvider` (nunca almacenado en el canal)
- **Auth fail-closed** por defecto (`AllowUnauthenticatedLab` requerido para omitir credenciales)
- Retry acotado en lecturas; **creates con un solo intento / no-blind-retry**
- Errores tipados: `TupagoUnknownOutcomeException`, `TupagoPendingReconciliationException`

## Inicio rápido

```csharp
using TuPago3.Sdk;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Transport;

var client = new TupagoPaymentClient(new TupagoClientOptions
{
    Target = "127.0.0.1:9090",
    UseTls = false,                      // solo loopback / lab
    Transport = TransportKind.Grpc,      // Grpc | Rest | GraphQl
    Credentials = new StaticTokenProvider(token),
    // AllowUnauthenticatedLab = true,   // solo para tests de lab explícitos
});

var perfil = await client.GetClienteAsync(new GetClienteRequest { Id = 1 });
await client.DisposeAsync();
```

## C2 CreateTelefonico — endpoint canónico

REST: `POST /api/payments/msjpagotelefonicousuario`

Usa `resolveSingleOwnedCliente()` — misma autoridad que gRPC/GraphQL.

**NO** usar `/msjpagotelefonico` (usa `requireOwnedClienteByKey`, autoridad diferente).

## ByCuenta en consultas de estatus

El backend3 ofrece dos formas de consultar el estatus de un pago:

| Variante | Cuándo usarla | Qué envía el SDK |
|----------|-----------------|-------------------|
| **ByCuenta** | El caller conoce la cuenta | `idCuenta` explícito en el body |
| **Usuario** | El backend resuelve la cuenta del JWT | Solo `idc` |

**Política del SDK:** si el request incluye `idCuenta` > 0, se usa **ByCuenta** automáticamente.

```csharp
// ByCuenta (recomendado)
var status = await client.GetQrStatusAsync(new GetStatusRequest
{
    Idc = "abc123",
    IdCuenta = 42,          // activa ByCuenta
});

// Rutas usadas:
// REST:  POST /api/payments/estatusmsjqr
// GraphQL: pagoQrEstatusByCuenta(idc, idCuenta)
```

## Invariantes financieras

`CreateQr` / `CreateTelefonico` realizan **como máximo un** dispatch por llamada pública (single-attempt / no-blind-retry).

- Errores de transporte ambiguos después de posible envío → `TupagoUnknownOutcomeException` (reconciliar con la misma idempotency key, no reintentar a ciegas).
- `TUPAGO_PENDING_RECONCILIATION` (ABORTED) → `TupagoPendingReconciliationException`.
- La idempotency key se preserva verbatim y nunca se regenera.

## Transportes

| Transporte | Estado | Notas |
|------------|--------|-------|
| gRPC | Completo | Transporte por defecto |
| REST | Completo | ByCuenta en status cuando `idCuenta` está presente |
| GraphQL | Completo | Operaciones ByCuenta + mutations |

Selecciona el transporte con `TupagoClientOptions.Transport`.

## Hardening aplicado

- Terminología corregida (single-attempt / no-blind-retry)
- Retry de lecturas reducido
- Sin límites comerciales inventados en Money
- `CANCELLED` ya no se trata como resultado definido en creates
- Auth fail-closed por defecto
- Token en blanco falla antes del dispatch
- TLS loopback sin confianza DNS/TOCTOU
- Validación de hostname más estricta
- El SDK no dispone el `LoggerFactory` del caller

## Tests

- Seleccción de transporte y auth fail-closed (`T30`)
- Shape de request REST (`T31`)
- Tests de integración opt-in contra backend3 (`T12`, `T12c`)

## Licencia

Propietaria. Distribución interna controlada únicamente.
