# Transport notes

## Status (this branch)

### Implemented
- gRPC (default)
- REST (`RestPaymentTransport`) — routes verified against backend3 `bbf06c02`:
  - C1: `POST /api/payments/msjpagoqrusuario` + `Idempotency-Key` (unwraps `mensaje`)
  - C2: `POST /api/payments/msjpagotelefonico` + `Idempotency-Key`
  - C3: `POST /api/payments/estatusmsjqr` (ByCuenta)
  - C4: `POST /api/payments/estatusmsjcobrotelefonico` (expected ByCuenta)
  - C5/C6: basic GET mapping (composition follow-up)
- GraphQL (`GraphQlPaymentTransport`) — operations verified against backend3 controller:
  - `cliente`, `catalogoInstitucion`
  - `pagoQrEstatusByCuenta` / `pagoTelefonicoEstatusByCuenta`
  - `createPagoQr` / `createPagoTelefonico`
  - Error mapping from `extensions.code` (IDEMPOTENCY_CONFLICT, PAYMENT_PENDING_RECONCILIATION, PAYMENT_LIMIT)

### Hardening already applied
- Terminology, retry policy, money bounds, CANCELLED classification
- Auth fail-closed, blank token, TLS loopback, hostname matching
- LoggerFactory ownership, SendTracking docs

### Remaining follow-ups
- Full Cliente composition (ClienteDTO + Catalogo) for REST C5
- Cross-transport conformance tests against real backend3
- Native transport replay audit
- Live test isolation for T12/T12c
- Fully async token path

All contracts were taken from backend3 source at anchor `bbf06c02bda06221b2f214075a4bf2bdf3345eb0`.
