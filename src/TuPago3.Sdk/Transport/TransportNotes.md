# Transport notes (foundation)

This folder holds the transport-neutral contract.

## Current status

- **gRPC**: fully implemented in `TupagoPaymentClient` (existing path).
- **REST**: not implemented. Expected routes (must be re-verified against current backend3):
  - C1 CreateQr: `POST /api/payments/msjpagoqrusuario` with `Idempotency-Key` header. Response may wrap under `mensaje`.
  - C2 CreateTelefonico: verify exact route.
  - C3 GetQrStatus: BY-CUENTA semantics. Expected: `/api/payments/estatusmsjqr`.
  - C4 GetTelefonicoStatus: BY-CUENTA. Expected: `/api/payments/estatusmsjcobrotelefonico`.
  - C5 GetCliente: `GET /api/clientes/{id}` (ClienteDTO) + catalog composition.
  - C6 GetCatalogo: `GET /api/catalogo-institucions/clave/{claveInstitucion}`.
- **GraphQL**: not implemented. Expected operations (must be re-verified):
  - `cliente(id: ID!)`
  - `catalogoInstitucion(claveInstitucion: String!)`
  - `pagoQrEstatusByCuenta(idc: String!, idCuenta: ID!)`
  - `pagoTelefonicoEstatusByCuenta(idc: String!, idCuenta: ID!)`
  - `createPagoQr(input, idempotencyKey)`
  - `createPagoTelefonico(input, idempotencyKey)`
  - Do **not** use `pagoQrEstatusUsuario` / `pagoTelefonicoEstatusUsuario` if they drop explicit idCuenta.

## Implementation rules (from program law)

- Creates: single-attempt / no-blind-retry. Ambiguous outcomes → UnknownOutcome.
- Idempotency key preserved verbatim.
- No invented commercial money bounds.
- Auth fail-closed by default in production.
- Redirects on create must not be followed; treat as UnknownOutcome where appropriate.
- Truncated success bodies on create → UnknownOutcome.
- All mocks and implementations must be derived from actual backend3 source, not synthetic payloads.

Full implementation of REST and GraphQL is a follow-up once backend3 wire shapes are re-validated.
