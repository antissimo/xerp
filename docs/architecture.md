# Architecture

Owner of this file: the architect. Decisions with alternatives are in `docs/adr/`. Vision: `docs/vision.md`.
Feature order: `docs/roadmap.md`. A spec may refine, but not contradict, this document.

## 1. Where the code is today and where it must go

The scaffold (commit `b587771`) is a single project `api/` with EF entities, a generic `CrudController`
and one migration. It has no layers, no tenant, no authentication and no tests, so it violates the project's own
rules. Spec 001 replaces it with the structure below. There is no deployed database, so migrations are
recreated rather than evolved (see spec 001).

## 2. Solution layout (ADR-0001)

```
Xerp.slnx (or Xerp.sln)        one solution file at the repository root
src/
  Xerp.Domain/                 entities, value objects, domain rules. No dependencies.
  Xerp.Application/            use cases (one operation = one class/method), input/output DTOs, validation,
                               ports (IXerpDb, ITenantContext, IClock ...). References Domain only.
  Xerp.Infrastructure/         EF Core DbContext, migrations, Npgsql, key hashing, clock. References Application.
  Xerp.Api/                    ASP.NET Core host: HTTP endpoints, authentication, error mapping, (later) MCP endpoint.
tests/
  Xerp.UnitTests/              Domain + Application rules, architecture (dependency) tests. No database.
  Xerp.IntegrationTests/       HTTP-level tests against the real API + a throwaway PostgreSQL.
```

Scope of this repository (owner decision 2026-10-08): the backend — the four projects above — and the MCP server
hosted in `Xerp.Api`. The CLI and the React web UI are separate projects in their own repositories; they consume
the HTTP API, which is therefore a published contract (versioned under `/api/v1`, stable error codes).

Dependency rule (enforced by a test):

| Project | May reference | Must not reference |
|---|---|---|
| Domain | BCL only | EF Core, ASP.NET Core, Npgsql, any other Xerp project |
| Application | Domain, `Microsoft.EntityFrameworkCore` (abstractions used through `IXerpDb`) | ASP.NET Core, Npgsql, Infrastructure, Api |
| Infrastructure | Application, Domain, EF Core, Npgsql | Api |
| Api | Application, Infrastructure (composition root only) | — |

Rules:
- **Business rules live only in Domain and Application.** An endpoint or MCP tool does three things:
  bind input, call one Application operation, map the result. If a rule can only be tested through HTTP, it is in
  the wrong layer.
- Application operations take plain DTOs and return a result (value or `AppError`); they never see `HttpContext`.
- Domain entities are never serialised directly to clients; Application returns DTOs.

## 3. Tenant isolation (ADR-0002)

- Model: one database, shared schema, a `TenantId` column on every table except `Tenants`.
- One tenant = one legal entity (company). Nothing is shared between tenants.
- The tenant comes **only** from the authenticated credential, exposed to Application as `ITenantContext`.
  It is never read from a URL, header, query string or body.
- Enforcement, all mandatory:
  1. Every tenant-owned entity implements one marker interface (`ITenantOwned`) with `TenantId`.
  2. The DbContext applies an EF Core global query filter `TenantId == current tenant` to every such entity.
  3. On save, the DbContext stamps `TenantId` on added entities and refuses to save an entity whose
     `TenantId` differs from the current tenant.
  4. Every unique index and every foreign key between tenant-owned tables includes `TenantId`
     (uniqueness is per tenant; a row can never reference another tenant's row). Foreign keys restrict
     deletes; nothing cascades (ADR-0008). A model test enforces this from spec 002 on.
  5. `IgnoreQueryFilters()` is forbidden outside the authentication lookup (which has no tenant yet).
  6. A model test asserts that every entity type except `Tenant` is tenant-owned and filtered.
- A record of another tenant is indistinguishable from a missing record: `404 NOT_FOUND`, never `403`.
- Every spec has tenant-isolation acceptance criteria and every feature has a tenant-isolation test.
- PostgreSQL row-level security is planned as a second line of defence (roadmap), not part of the foundation.

## 4. Identity and authentication (ADR-0003)

- Clients authenticate with an API key: `Authorization: Bearer xerp_<secret>`.
- A key belongs to exactly one tenant and has an `actorType`: `human` or `agent`. This is what makes
  "AI is a user" concrete: an agent holds its own key and its actions are attributable.
- Only a SHA-256 hash of the key is stored. The plaintext is returned once, at creation.
- Tenants are provisioned through `/api/v1/admin/*`, authenticated with a platform admin key taken from
  configuration (`Xerp:AdminKey`, env `Xerp__AdminKey`). The admin key has no tenant and cannot call tenant routes.
- Every write stamps `CreatedBy` / `UpdatedBy` with the acting API key id.
- Key lifecycle (ADR-0010, spec 003): a tenant key can create, list and revoke keys of its tenant. Revocation
  is permanent; keys are never deleted (they are the actors that audit fields point at); a key cannot revoke
  itself, so a tenant always keeps one active key. A plaintext key is returned once, over HTTP only — never
  through an MCP tool.
- Owner decisions (2026-10-09): until the permissions spec any tenant key may manage keys; MCP clients use a
  static API key (no OAuth yet); there is no rate limiting yet.
- Roles/permissions per key are a later roadmap item. Human login for the
  external web client (e.g. OIDC mapped to the same actor model) is not planned here until that project asks for it.
- Agents may post documents by default (owner answered "yes"; interpreted as "agents may post by default"),
  restrictable per key once permissions exist. Until permissions exist, every tenant key can do everything inside its tenant.

## 5. HTTP API conventions

- Base path `/api/v1`. `/health` is outside it and unauthenticated.
- JSON, camelCase properties, UTF-8. Timestamps are UTC ISO 8601. Ids are server-generated UUIDv7.
- Unknown JSON properties in a request body are rejected (`400 VALIDATION_FAILED`) so that an agent's typo is
  an error, not a silently ignored field. The same holds for query strings: a query parameter an operation does
  not define is rejected under its own name. Property and parameter names are case-sensitive.
- No request may fail in the database because of its content; `500` is never the answer to client input.
  Single-line text (codes, names) contains no control characters; multi-line text allows only LF, CR and TAB.
- Under `/api/v1` the kind of credential is checked before the route: `/api/v1/admin/*` needs the admin key,
  everything else a tenant key (`403` otherwise), whether or not the path exists. An unknown path and an
  unsupported method on a known path are both `404 NOT_FOUND`.
- Collections: `GET` returns `{ "items": [...], "total": n, "limit": n, "offset": n }`;
  `limit` default 50, range 1–500; `offset` >= 0; out-of-range values are rejected, not clamped.
- Master data has a server id (`id`, used for references) and a human/agent-friendly `code`, unique per tenant,
  case-insensitive. Lookup by code is `GET /<resource>/by-code/{code}`.
- Create: `POST` -> `201` + `Location` + body. Replace: `PUT` (all fields) -> `200` + body. Delete: `204`.
- Optional text (ADR-0011): omitted on create, `null`, empty and whitespace-only all mean "no value", stored
  and returned as `null`; the property is always present in a representation and must be present (possibly
  `null`) on `PUT`. An address is six optional flat fields — `addressLine1`, `addressLine2`, `postalCode`,
  `city`, `region`, `countryCode` (two upper-case letters, form only) — with the same rules on every record
  that has one.
- References between records (ADR-0008): a request names another record by server id in a field `<role>Id`
  (`baseUnitId`); a representation returns it as an embedded summary `"<role>": { "id", "code", "name" }`.
  An inactive record cannot be newly referenced but existing references stay valid. A referenced record cannot
  be deleted (`IN_USE`); it is retired with `isActive = false`. Every spec that adds a reference adds a list
  filter by it (`GET /articles?baseUnitId=`).
- Order of checks in a write: validation (`400`) -> addressed record exists (`404`) -> state
  (`409 INVALID_STATE`) -> references (`409 REFERENCE_*`) -> uniqueness (`409 CODE_TAKEN`).
- Documents (ADR-0007, ADR-0012): a document has a header and `lines`; it is created and replaced as a whole
  while `draft`, and changes state through action routes (`POST /<resource>/{id}/post`). A posted document is
  immutable and has a `number`, `postedAt`, `postedBy`; lookup by number is `GET /<resource>/by-number/{number}`.
  An error about a line is keyed `lines[i].<field>` with a zero-based index into the request's array.
- Warehouses on documents (ADR-0019): every document type has exactly one mandatory warehouse in its header
  (a transfer: source and destination) — the types that exist and every type added later. Each tenant has
  exactly one default warehouse (`isDefault`), created with the tenant and always active. On **create**,
  `warehouseId` omitted or `null` means the default warehouse at that moment (on a stock document linked to
  an order: the order's warehouse); the resolved warehouse is stored and returned. On replace, and for both
  warehouses of a transfer, the caller names it.
- Orders (ADR-0016): a purchase or sales order is a document that moves no stock. It is edited while `draft`,
  gets its `number` when confirmed (`POST /<resource>/{id}/confirm`) and is then immutable; `close` and
  `reopen` switch it between `confirmed` and `closed`. Goods are received or delivered by a stock document
  linked to the order (`purchaseOrderId` / `salesOrderId` on the header, `orderLineNo` on each line); progress
  per order line is kept in base units and never exceeds the ordered quantity.
  Stock on hand shows, per (article, warehouse), `quantity` (the stored balance, equal to the ledger sum), `incomingQuantity` (outstanding
  on confirmed purchase orders), `reservedQuantity` (outstanding on confirmed sales orders) and
  `availableQuantity` (`quantity − reservedQuantity`, may be negative). Reservation informs and blocks nothing
  (ADR-0017).
- Prices and amounts are exact decimals in the tenant's one currency: a unit price has at most 6 decimal
  places; an amount is rounded to 2 decimal places, half away from zero.
- Quantities are exact decimals sent as JSON numbers: at most 6 decimal places and 15 significant digits;
  a quoted number is a wrong type. Consumers compare numerically (`10` equals `10.000000`).

## 6. Error model (ADR-0004)

Every non-2xx response under `/api/v1` is `application/problem+json` (RFC 9457) with a stable `code`:

```json
{ "type": "about:blank", "title": "VALIDATION_FAILED", "status": 400,
  "detail": "One or more fields are invalid.", "code": "VALIDATION_FAILED",
  "errors": { "code": ["Code is required."] } }
```

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Malformed JSON, unknown property, or field rule violated — anything decidable from the request alone. `errors` maps camelCase field name -> messages. |
| 401 | `UNAUTHENTICATED` | Missing, malformed, unknown or disabled credential. Sent with `WWW-Authenticate: Bearer`. |
| 403 | `FORBIDDEN` | Valid credential, operation not allowed for it. |
| 404 | `NOT_FOUND` | No such record in this tenant (also: other tenant's record, malformed id), no such path, or method not supported on the path. |
| 409 | `CODE_TAKEN` | Unique code already used in this tenant. |
| 409 | `IN_USE` | Record is referenced and cannot be deleted; or a field that is frozen while the record is used would change (`errors` names the fields). |
| 409 | `REFERENCE_NOT_FOUND` | A `<role>Id` in the body is well-formed but no such record exists in this tenant (also: other tenant's record). `errors` has the field's key. |
| 409 | `REFERENCE_INACTIVE` | A `<role>Id` in the body points at an inactive record that is being newly assigned. `errors` has the field's key. |
| 409 | `CANNOT_REVOKE_SELF` | An API key tried to revoke itself. |
| 409 | `DEFAULT_WAREHOUSE` | The operation would leave the tenant without an active default warehouse: delete or deactivation of the default warehouse, or making an inactive warehouse the default (spec 011). `errors` has `isActive` except on delete. |
| 409 | `INVALID_STATE` | Operation not allowed in the document's current status (e.g. replace, delete or post of a posted document; reversal of a draft, of a reversed or of a reversing document; replace of a confirmed order; close of a draft order). |
| 409 | `INSUFFICIENT_STOCK` | Posting would make stock on hand negative. `errors` has `lines[i].quantity` for the short lines. |
| 409 | `ARTICLE_NOT_STOCKED` | A stock document line names a `service` article. `errors` has `lines[i].articleId`. |
| 409 | `UNIT_IS_BASE_UNIT` | A unit conversion was set for the article's own base unit (spec 007). `errors` has `unitId`. |
| 409 | `UNIT_NOT_ON_ARTICLE` | A document line's unit is neither the base unit nor an alternative unit of its article. `errors` has `lines[i].unitId`. |
| 409 | `QUANTITY_NOT_CONVERTIBLE` | A line's quantity converts to a base quantity of zero or above the maximum. `errors` has `lines[i].quantity`. |
| 409 | `COUNT_OUTDATED` | Posting a stock count whose book quantity no longer equals stock on hand (spec 008). `errors` has `lines[i].quantity` for the outdated lines. |
| 409 | `PARTNER_ROLE_MISSING` | The partner named on an order lacks the role the order needs (`isSupplier` / `isCustomer`). `errors` has `supplierId` / `customerId`. |
| 409 | `ORDER_NOT_OPEN` | A stock document is saved or posted against an order that is not `confirmed`. `errors` has `purchaseOrderId` / `salesOrderId`. |
| 409 | `ORDER_MISMATCH` | A stock document linked to an order names another warehouse, or a line's article is not its order line's. `errors` has `warehouseId` and/or `lines[i].articleId`. |
| 409 | `QUANTITY_EXCEEDS_ORDER` | Posting would take an order line above its ordered quantity. `errors` has `lines[i].quantity` for the lines linked to it. |
| 413 | `PAYLOAD_TOO_LARGE` | The request body is larger than 1 048 576 bytes (1 MB), declared or counted while read. Refused before anything is parsed or applied; no `errors`. Checked after the credential and before routing, on `/api/v1` and on `/mcp` (review of open item 001/7). |
| 500 | `INTERNAL_ERROR` | Unexpected. No stack trace or SQL in the body. |

`code` values are part of the contract: clients and tests branch on `code`, never on `detail` text.
Specs add feature-specific codes; this table is the registry and is updated with them.
Application defines the errors (`AppError { Code, Detail, Errors }`); Api maps code -> HTTP status in one place;
MCP maps the same error to a tool error with the same `code` (shape in section 7).
Exceptions on `/mcp`: `UNAUTHENTICATED`, `FORBIDDEN` and `PAYLOAD_TOO_LARGE` are decided before any tool runs
and are answered as HTTP problem documents, not as tool errors (ADR-0009).

## 7. API and MCP; external clients (ADR-0005 amended, ADR-0009)

```
 Web UI (separate project) ──HTTP──┐
 CLI    (separate project) ──HTTP──┤
                                   ├─► Xerp.Api ──► Xerp.Application ──► Xerp.Domain
 Agent ───────────────MCP (HTTP)───┘      (HTTP endpoints and MCP tools           │
                                           are siblings, both thin)        Xerp.Infrastructure ──► PostgreSQL
```

- The MCP server is hosted inside `Xerp.Api` (Streamable HTTP at `/mcp`), authenticated by the same API key,
  and calls Application operations in-process. It does not call the HTTP API and has no rules of its own.
- MCP tools are named `<resource>_<verb>` in snake_case (`uom_list`, `article_create`). One tool = one
  Application operation = one HTTP endpoint, with the same field names, limits and error codes.
- MCP shape (ADR-0009): official C# SDK; stateless (no MCP session; every request carries the API key);
  tools only. Authentication and `Origin` failures are HTTP `401`/`403` problem documents. A tool's arguments
  are the HTTP operation's path, query and body fields in one closed JSON object.
  Success: `structuredContent` = the HTTP response body (or `{ "deleted": true }` for `204`), plus the same JSON
  as one text block; every tool declares an `outputSchema`.
  Error: `isError: true` and one text block containing `{ "code", "detail", "errors"? }`, no
  `structuredContent`. Validation failures are tool errors (`VALIDATION_FAILED`), never JSON-RPC errors.
- Operations deliberately without an MCP tool: tenant provisioning (admin key, no tenant context) and API key
  creation (its result is a secret; ADR-0010). Every other operation has a tool; a test pins the exact tool list.
- The CLI and the web UI are not built in this repository. They are ordinary HTTP API clients with their own
  API keys; nothing in the backend is specific to them. Because they are developed separately, the HTTP API must
  not change incompatibly within `/api/v1`, and the OpenAPI document is the description they build against.
  It does not exist yet: spec 019 delivers it (served in every environment to any authenticated tenant key,
  and committed to the repository so that a contract change is visible in a diff).
- Every spec defines both the HTTP and the MCP signature of each operation. Specs 001 and 002 carry their MCP
  signatures as contract only; spec 003 implements them. From spec 004 on, a spec's tools are implemented with it,
  including tool metadata (description with error codes, schemas, annotations) and an HTTP/MCP parity test.

## 8. Data conventions

- EF Core + Npgsql, code-first migrations in `Xerp.Infrastructure`. Migrations are applied at API startup
  (acceptable while there is one instance; revisit before production).
- Money and quantities are `decimal` / `numeric`, never floating point.
- Audit columns on every tenant-owned row that records an act (ledger entries carry `PostedAt` / `PostedBy`
  instead; stored projections carry none): `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`.
  Columns that hold an actor (`CreatedBy`, `UpdatedBy`, `RevokedBy`, …) are real foreign keys
  `(TenantId, <column>)` -> `ApiKeys (TenantId, Id)` with `ON DELETE RESTRICT`: an actor can never belong to
  another tenant, and an API key that has written anything can be revoked but never deleted (ADR-0010).
- Ledger tables (stock ledger from spec 005; journal lines later) are append-only: no `UPDATE`, no `DELETE`;
  corrections are reversing entries (ADR-0007). Stock on hand is the sum of the stock ledger and is never
  negative (ADR-0012).
- Stored projections (ADR-0018): `StockBalance` holds the quantity per (tenant, warehouse, article). The
  ledger is the truth and the balance its copy: written only together with ledger entries, in the same
  transaction and under the same per-tenant lock, and by the rebuild operation; read by everything that
  shows or decides on stock. Invariant, tested also under parallel postings: stored == sum of the ledger.
  A projection has no audit columns; it must come with an operation that verifies it against its ledger and
  one that rebuilds it. Later projections (value of stock, account balances) follow the same pattern. Document numbers are gapless per tenant and document type and are assigned at posting
  (stock documents) or at confirmation (orders).

## 9. Testing (ADR-0006)

- xUnit. Test-first. From spec 003 on the acceptance tests are written by the **tester** (`tester.md`), on
  branch `tests/NNN-name`, before the builder implements; the builder merges that branch and may not change
  those tests. (Specs 001 and 002: the builder wrote them.)
- Consequence for specs: an acceptance criterion is **black-box** — stated as requests and observable responses
  on the public surface (HTTP `/api/v1`, MCP `/mcp`), with nothing that requires an internal type, the EF model
  or a look into the database. A black-box test may configure the test host (settings such as
  `Xerp:AdminKey`) and uses only operations that exist: tenants from `POST /api/v1/admin/tenants`, further
  keys from `POST /api/v1/api-keys`.
- What cannot be observed from outside (layering, the EF model, database constraints as a second barrier,
  hashing at rest) is a criterion marked *(builder)*: the builder writes that test. *(manual)* criteria are
  checked in review. A spec keeps *(builder)* criteria few; a business rule that needs one is in the wrong
  place or is stated wrongly.
- Unit tests: Domain/Application rules and the architecture dependency test. No I/O.
- Integration tests: start the real API in-process (`WebApplicationFactory`) against a PostgreSQL started by
  Testcontainers (`postgres:18`), apply the real migrations, drive it over HTTP. Never the dev database, never
  an in-memory or SQLite provider (query filters, unique indexes and collation behaviour must be the real ones).
- Tests must be independent: each creates its own tenants (unique codes), so tests can share one database
  container and run in parallel.
- Each acceptance criterion maps to at least one named test. Each feature has a tenant-isolation test, over
  HTTP and over MCP.
- **Inventory tests.** A test that holds a complete list of what exists (the literal tool list, a count of
  catalogue entries) is updated by whoever adds to the inventory; the spec that defines the addition approves
  it, and no separate approval is needed. Removing a name or an assertion still needs explicit approval.
  The tool list stays literal on purpose: an extra or missing tool must fail the build.
- The table test holds no literal list: it reads the tables from the database, asserts the tenant invariant
  (section 3) on every table except `Tenants`, and asserts that the tables the specs name are among them.

## 10. Build environment constraint

The host has **no .NET SDK**. `dotnet build` and `dotnet test` run inside `mcr.microsoft.com/dotnet/sdk:10.0`
(present locally, SDK 10.0.401); `postgres:18` is present locally too.

Integration tests start containers from inside that SDK container, so it needs the host's Docker socket and a
network path to the ports Testcontainers publishes. Suggested invocation (**not yet verified by the architect —
the builder must make it work in spec 001 and report what was needed**):

```
docker run --rm --network host \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_HOST_OVERRIDE=localhost \
  -v "$PWD":/src -w /src \
  -v xerp-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

Requirements for whatever wrapper spec 001 delivers (`scripts/dotnet.sh <args>`): `build` and `test` work from a
clean checkout; no root-owned `bin/`/`obj/` files are left in the worktree; NuGet packages are cached between
runs. Testcontainers also pulls a small helper image (`testcontainers/ryuk`) on first run, so the first run needs
network access. If Docker-in-Docker access turns out to be impossible in the builder's sandbox, the builder
raises it in `docs/questions/` instead of switching to a non-PostgreSQL test database.

`compose.yaml` remains the way to run the system locally (`docker compose up --build`); the database port stays
unpublished.
