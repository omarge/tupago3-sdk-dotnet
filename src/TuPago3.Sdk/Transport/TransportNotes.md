# Transport notes (foundation)

This folder holds the transport-neutral contract.

## Current status (this branch)

### Applied hardening
- Terminology corrected (single-attempt / no-blind-retry).
- Read retry narrowed to Unavailable + DeadlineExceeded.
- Money: no invented commercial bounds.
- CANCELLED no longer treated as definite create outcome.
- Auth fail-closed by default (`AllowUnauthenticatedLab` required for null credentials).
- Blank token fails closed before dispatch.
- TLS loopback: DNS/TOCTOU trust removed; only explicit local identities.
- Hostname matching: tighter wildcard + no CN fallback when SAN exists.
- LoggerFactory ownership: SDK no longer disposes caller-supplied factory.
- SendTracking documentation corrected (does not prove bytes on wire).
- Transport selection field added to options (Grpc only currently accepted).

### Still missing / follow-up required
- Full REST C1–C6 implementation (requires verified backend3 wire shapes).
- Full GraphQL C1–C6 implementation (ByCuenta semantics; requires verified schema).
- Wiring of selected transport into the client beyond the current guard.
- Cross-transport conformance tests.
- Native Grpc.Net.Client / SocketsHttpHandler replay audit under adversarial conditions.
- Total public-call deadline budget (vs per-attempt).
- Live test isolation retirement or strict opt-in for T12/T12c.
- Fully async token path (remove remaining sync-over-async).

## Expected contracts (must be re-verified against current backend3)

### REST
- C1 CreateQr: `POST /api/payments/msjpagoqrusuario` with `Idempotency-Key`. Response may wrap under `mensaje`.
- C2 CreateTelefonico: verify exact route.
- C3 GetQrStatus: BY-CUENTA. Expected `/api/payments/estatusmsjqr`.
- C4 GetTelefonicoStatus: BY-CUENTA. Expected `/api/payments/estatusmsjcobrotelefonico`.
- C5 GetCliente: `GET /api/clientes/{id}` + catalog composition.
- C6 GetCatalogo: `GET /api/catalogo-institucions/clave/{claveInstitucion}`.

### GraphQL
- `cliente(id: ID!)`
- `catalogoInstitucion(claveInstitucion: String!)`
- `pagoQrEstatusByCuenta(idc: String!, idCuenta: ID!)`
- `pagoTelefonicoEstatusByCuenta(idc: String!, idCuenta: ID!)`
- `createPagoQr(input, idempotencyKey)`
- `createPagoTelefonico(input, idempotencyKey)`
- Do **not** use usuario convenience routes if they drop explicit idCuenta.

Full implementation of REST and GraphQL must be derived from actual backend3 source.
