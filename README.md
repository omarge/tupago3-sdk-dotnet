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
\n\n---\n\n# Implementation manual / Manual de implementación\n\n## Manual ES — Implementación paso a paso\n\n**Índice:** [1. Requisitos](#manual-es--implementación-paso-a-paso) · [2. Instalación](#manual-es--implementación-paso-a-paso) · [3. Configuración](#manual-es--implementación-paso-a-paso) · [4. Ejemplo](#manual-es--implementación-paso-a-paso) · [5. Contrato C1–C6](#contrato-de-referencia--reference-contract) · [6. Seguridad y errores](#reglas-financieras--financial-safety-rules) · [7. Pruebas y distribución](#pruebas-y-aceptación--testing-and-acceptance)\n\n1. **Requisitos:** .NET 10 / NuGet. Disponer de acceso autorizado a backend3, credenciales JWT válidas y ambiente HTTPS, o laboratorio loopback explícitamente habilitado.\n2. **Instalación:** `.NET SDK 10`: `dotnet restore && dotnet test -c Release && dotnet pack src/TuPago3.Sdk/TuPago3.Sdk.csproj -c Release -o artifacts/nupkg`. Add generated `.nupkg` to a **separate** .NET project via a local NuGet source, without `ProjectReference`.\n3. **Configuración:** seleccionar el transporte soportado; suministrar endpoint real y JWT mediante variable de entorno o proveedor de credenciales. No usar puertos de laboratorio como defaults productivos.\n4. **Ejemplo de inicio:** ejecutar el ejemplo de abajo con los imports/exports públicos propios del SDK; sustituir los hostnames de ejemplo y consultar primero C5/C6.\n5. **Contrato:** consultar tabla C1–C6; en C3 y C4 conservar `idc` e `idCuenta`, y en C5 exigir perfil compuesto.\n6. **Errores y seguridad:** diferenciar `UnknownOutcome`, `PendingReconciliation`, `NOT_FOUND`, autenticación y permisos. Nunca reintentar creaciones automáticamente.\n7. **Validación:** `dotnet test -c Release`, then `dotnet pack`; install package from local NuGet feed and call C3–C6. Opt-in real gRPC backend3 tests are T12/T12c; never mistake skipped tests for real E2E success.\n\n### Ejemplo / Example (.NET 10 / NuGet)\n\n\`\`\`csharp\nusing TuPago3.Sdk;
using TuPago3.Sdk.Authentication;
using TuPago3.Sdk.Client;
using TuPago3.Sdk.Transport;
var jwt = Environment.GetEnvironmentVariable("TUPAGO_JWT")
    ?? throw new InvalidOperationException("TUPAGO_JWT missing");
var sdk = new TupagoPaymentClient(new TupagoClientOptions {
    Target = "backend.example.com:443", UseTls = true,
    Transport = TransportKind.Grpc, Credentials = new StaticTokenProvider(jwt)
});
try { var perfil = await sdk.GetClienteAsync(new GetClienteRequest { Id = 1 }); }
finally { await sdk.DisposeAsync(); }\n\`\`\`\n\n## Manual EN — Step-by-step implementation\n\n**Contents:** [1. Prerequisites](#manual-en--step-by-step-implementation) · [2. Install](#manual-en--step-by-step-implementation) · [3. Configure](#manual-en--step-by-step-implementation) · [4. Example](#manual-en--step-by-step-implementation) · [5. C1–C6 wire contract](#contrato-de-referencia--reference-contract) · [6. Safety and errors](#reglas-financieras--financial-safety-rules) · [7. Verify and distribute](#pruebas-y-aceptación--testing-and-acceptance)\n\n1. **Prerequisites:** .NET 10 / NuGet; authorized backend3 credentials and a TLS-enabled endpoint (or explicitly authorized loopback lab).\n2. **Install:** `.NET SDK 10`: `dotnet restore && dotnet test -c Release && dotnet pack src/TuPago3.Sdk/TuPago3.Sdk.csproj -c Release -o artifacts/nupkg`. Add generated `.nupkg` to a **separate** .NET project via a local NuGet source, without `ProjectReference`.\n3. **Configure:** choose a supported transport; supply the endpoint and JWT through environment-backed credential providers. Do not copy lab hosts or ports into production.\n4. **Minimal example:** follow the SDK-specific example below. Replace placeholder endpoints and start with a C5/C6 read.\n5. **Contract:** consult the exact C1–C6 wire table. Preserve `idc`/`idCuenta` and require the full composed C5 profile.\n6. **Errors and security:** distinguish unknown financial outcomes, pending reconciliation, not-found and auth/ownership failures. Never automatically replay creates.\n7. **Verification:** `dotnet test -c Release`, then `dotnet pack`; install package from local NuGet feed and call C3–C6. Opt-in real gRPC backend3 tests are T12/T12c; never mistake skipped tests for real E2E success.\n\n## Contrato de referencia / Reference contract

**ES.** Autoridad: backend3 `omarge/tupagoback3` `bbf06c02bda06221b2f214075a4bf2bdf3345eb0`. El JWT determina identidad y alcance de propietario; jamás hardcodear secretos. `idc`, `idCuenta` y claves bancarias son identificadores: conservar su representación y ceros iniciales.

**EN.** Authority: backend3 at the pinned commit above. JWT determines caller identity/ownership. Never hardcode credentials. Preserve IDs and leading zeros; do not coerce long IDs through floating-point numbers.

| Operación / Operation | REST backend3 | GraphQL backend3 | gRPC |
|---|---|---|---|
| C1 CreateQr | `POST /api/payments/msjpagoqrusuario` | `createPagoQr` | `CreateQr` |
| C2 CreateTelefonico | `POST /api/payments/msjpagotelefonicousuario` | `createPagoTelefonico` | `CreateTelefonico` |
| C3 GetQrStatus | `POST /api/payments/estatusmsjqr` JSON `{"idc":"...","idCuenta":42}` | `pagoQrEstatusByCuenta(idc,idCuenta)` | `GetQrStatus` |
| C4 GetTelefonicoStatus | `POST /api/payments/estatusmsjcobrotelefonico` JSON `{"idCuenta":42,"idMsjdeCobro":"..."}` | `pagoTelefonicoEstatusByCuenta(idc,idCuenta)` | `GetTelefonicoStatus` |
| C5 GetCliente | `GET /api/clientes/{id}` **then** `GET /api/catalogo-institucions/clave/{claveInstitucion}` | `getCliente`/equivalent schema query | `GetCliente` |
| C6 GetCatalogo | `GET /api/catalogo-institucions/clave/{clave}` | `catalogoInstitucion` | `GetCatalogo` |

**ES.** En C4, `idMsjdeCobro` corresponde al `idc` público. C5 debe devolver `ClientePerfil` **compuesto**, no solamente el `ClienteDTO`. Los nombres de métodos del SDK pueden diferir de los nombres wire; usar las APIs públicas mostradas abajo.

**EN.** In C4, `idMsjdeCobro` is the public request's `idc`. C5 must return the **composed** `ClientePerfil`, not just `ClienteDTO`. SDK method names and wire operation names may differ.

### Reglas financieras / Financial safety rules

1. **ES:** C1/C2 tienen **un solo intento** por llamada; nunca activar reintentos automáticos de creación. **EN:** C1/C2 are single-attempt; never enable automatic creation retries.
2. **ES:** Guardar la clave de idempotencia original de cada pago. Un timeout, corte de conexión, 5xx, redirección o respuesta de éxito inválida después del posible envío es `UnknownOutcome`: **consultar estado/reconciliar**, no reenviar ciegamente ni generar otra clave. **EN:** Persist the caller-owned idempotency key. Post-dispatch ambiguity is `UnknownOutcome`: reconcile with the original identity, never blindly resend or mint a different key.
3. **ES:** `PendingReconciliation` (REST 202 autorizado, GraphQL `PAYMENT_PENDING_RECONCILIATION`, gRPC `ABORTED` + `TUPAGO_PENDING_RECONCILIATION`) no significa éxito ni autoriza reintento. **EN:** Pending is neither success nor permission to retry.
4. **ES:** C3–C6 son lecturas; sólo los errores transitorios elegibles pueden reintentarse dentro del límite de política. Errores de JWT, permisos, validación, conflicto o inexistencia no. **EN:** Only eligible transient read errors may be retried within a bounded policy; deterministic failures must not.
5. **ES:** TLS con verificación de identidad fuera de loopback; plaintext únicamente en laboratorio local explícito cuando el adaptador lo permita. Tokens sólo por proveedor seguro/entorno. **EN:** Verify TLS identity for remote targets; explicit local loopback lab exceptions only. Never ship lab keys or tokens.
6. **ES:** Backend3/PostgreSQL sigue siendo la autoridad financiera; el SDK no confirma liquidaciones. **EN:** Backend3/PostgreSQL remains the financial authority; an SDK response is not settlement proof.

### Pruebas y aceptación / Testing and acceptance

**ES — Secuencia:** (1) compilar y ejecutar tests, (2) construir paquete exacto, (3) instalarlo en consumidor **fuera del árbol fuente**, (4) probar JWT válido/ausente/inválido y C3–C6 contra backend3 aislado, (5) verificar ownership y `NOT_FOUND`, (6) C1/C2 solamente en sandbox aprobado sin pagos reales, (7) registrar HEAD, CI y SHA256. No afirmar E2E de transportes no probados.

**EN — Sequence:** (1) build and test, (2) produce the exact artifact, (3) install into a separate **out-of-tree** consumer, (4) verify valid/missing/invalid JWT and C3–C6 against an isolated real backend3, (5) verify ownership and typed `NOT_FOUND`, (6) test C1/C2 only in an explicitly approved non-production financial sandbox, (7) record HEAD, CI and SHA256. Do not claim untested transports as E2E-certified.

**Estado / Status:** local and read-only E2E certificates cover their **documented transports**, not production financial writes, Banxico/STP external calls, or real Microsoft Office host validation.
\n