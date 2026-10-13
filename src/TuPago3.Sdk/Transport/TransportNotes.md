# Transport notes

## ByCuenta vs Usuario en consultas de estatus

El backend3 expone dos variantes para consultar el estatus de un pago:

| Variante | Cuándo usarla | Ruta REST (QR) | GraphQL |
|----------|-----------------|----------------|---------|
| **ByCuenta** | El caller conoce y envía `idCuenta` explícitamente | `POST /api/payments/estatusmsjqr` | `pagoQrEstatusByCuenta` |
| **Usuario** | El backend debe resolver la cuenta del JWT | `POST /api/payments/estatusmsjqrusuario` | `pagoQrEstatusUsuario` |

### Política del SDK

- Si el request incluye `idCuenta` > 0, el SDK usa **ByCuenta**.
- Esto cumple con la program law del audit: cuando la cuenta es conocida, no se debe ocultar.
- La variante Usuario queda disponible en backend3 para callers que no envíen `idCuenta`.

### Ejemplo

```csharp
// ByCuenta (recomendado cuando idCuenta es conocido)
var status = await client.GetQrStatusAsync(new GetStatusRequest
{
    Idc = "abc123",
    IdCuenta = 42,          // activa ByCuenta
});
```

## Estado de transportes

### Implementado
- gRPC (default)
- REST (`RestPaymentTransport`) — rutas verificadas contra backend3 `bbf06c02`:
  - C1: `POST /api/payments/msjpagoqrusuario` + `Idempotency-Key` (unwraps `mensaje`)
  - C2: `POST /api/payments/msjpagotelefonico` + `Idempotency-Key`
  - C3: `POST /api/payments/estatusmsjqr` (**ByCuenta** cuando `idCuenta` presente)
  - C4: `POST /api/payments/estatusmsjcobrotelefonico` (**ByCuenta**)
  - C5/C6: basic GET mapping (composition follow-up)
- GraphQL (`GraphQlPaymentTransport`) — operaciones verificadas:
  - `cliente`, `catalogoInstitucion`
  - `pagoQrEstatusByCuenta` / `pagoTelefonicoEstatusByCuenta`
  - `createPagoQr` / `createPagoTelefonico`
  - Error mapping from `extensions.code`

### Hardening aplicado
- Terminología, retry policy, money bounds, CANCELLED classification
- Auth fail-closed, blank token, TLS loopback, hostname matching
- LoggerFactory ownership, SendTracking docs

### Remaining follow-ups
- Full Cliente composition (ClienteDTO + Catalogo) para REST C5
- Live cross-transport conformance tests contra backend3 real
- Native transport replay audit

All contracts were taken from backend3 source at anchor `bbf06c02bda06221b2f214075a4bf2bdf3345eb0`.
