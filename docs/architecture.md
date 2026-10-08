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
  Xerp.Cli/                    (later) command-line client over the HTTP API.
tests/
  Xerp.UnitTests/              Domain + Application rules, architecture (dependency) tests. No database.
  Xerp.IntegrationTests/       HTTP-level tests against the real API + a throwaway PostgreSQL.
web/                           (later) React + TypeScript client.
```

Dependency rule (enforced by a test):

| Project | May reference | Must not reference |
|---|---|---|
| Domain | BCL only | EF Core, ASP.NET Core, Npgsql, any other Xerp project |
| Application | Domain, `Microsoft.EntityFrameworkCore` (abstractions used through `IXerpDb`) | ASP.NET Core, Npgsql, Infrastructure, Api |
| Infrastructure | Application, Domain, EF Core, Npgsql | Api |
| Api | Application, Infrastructure (composition root only) | — |

Rules:
- **Business rules live only in Domain and Application.** An endpoint, MCP tool or CLI command does three things:
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
     (uniqueness is per tenant; a row can never reference another tenant's row).
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
- Roles/permissions per key, key management by tenant users, and human login (OIDC) for the web UI are later
  roadmap items. Until permissions exist, every tenant key can do everything inside its tenant.

## 5. HTTP API conventions

- Base path `/api/v1`. `/health` is outside it and unauthenticated.
- JSON, camelCase properties, UTF-8. Timestamps are UTC ISO 8601. Ids are server-generated UUIDv7.
- Unknown JSON properties in a request body are rejected (`400 VALIDATION_FAILED`) so that an agent's typo is
  an error, not a silently ignored field.
- Collections: `GET` returns `{ "items": [...], "total": n, "limit": n, "offset": n }`;
  `limit` default 50, range 1–500; `offset` >= 0; out-of-range values are rejected, not clamped.
- Master data has a server id (`id`, used for references) and a human/agent-friendly `code`, unique per tenant,
  case-insensitive. Lookup by code is `GET /<resource>/by-code/{code}`.
- Create: `POST` -> `201` + `Location` + body. Replace: `PUT` (all fields) -> `200` + body. Delete: `204`.

## 6. Error model (ADR-0004)

Every non-2xx response under `/api/v1` is `application/problem+json` (RFC 9457) with a stable `code`:

```json
{ "type": "about:blank", "title": "VALIDATION_FAILED", "status": 400,
  "detail": "One or more fields are invalid.", "code": "VALIDATION_FAILED",
  "errors": { "code": ["Code is required."] } }
```

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Malformed JSON, unknown property, or field rule violated. `errors` maps camelCase field name -> messages. |
| 401 | `UNAUTHENTICATED` | Missing, malformed, unknown or disabled credential. Sent with `WWW-Authenticate: Bearer`. |
| 403 | `FORBIDDEN` | Valid credential, operation not allowed for it. |
| 404 | `NOT_FOUND` | No such record in this tenant (also: other tenant's record, malformed id). |
| 409 | `CODE_TAKEN` | Unique code already used in this tenant. |
| 409 | `IN_USE` | Record is referenced and cannot be deleted. |
| 409 | `INVALID_STATE` | (later) Operation not allowed in the document's current status. |
| 500 | `INTERNAL_ERROR` | Unexpected. No stack trace or SQL in the body. |

`code` values are part of the contract: clients and tests branch on `code`, never on `detail` text.
Specs add feature-specific codes; this table is the registry and is updated with them.
Application defines the errors (`AppError { Code, Detail, Errors }`); Api maps code -> HTTP status in one place;
MCP maps the same error to a tool error with the same `code`.

## 7. API, MCP and CLI (ADR-0005)

```
 Web (React) ──HTTP──┐
 CLI ─────────HTTP───┤
                     ├─► Xerp.Api ──► Xerp.Application ──► Xerp.Domain
 Agent ──MCP (HTTP)──┘      (HTTP endpoints and MCP tools           │
                             are siblings, both thin)        Xerp.Infrastructure ──► PostgreSQL
```

- The MCP server is hosted inside `Xerp.Api` (Streamable HTTP at `/mcp`), authenticated by the same API key,
  and calls Application operations in-process. It does not call the HTTP API and has no rules of its own.
- MCP tools are named `<resource>_<verb>` in snake_case (`uom_list`, `article_create`). One tool = one
  Application operation = one HTTP endpoint, with the same field names, limits and error codes.
- The CLI is a separate executable that talks to the HTTP API (so it works against a remote server).
- Every spec defines both the HTTP and the MCP signature of each operation. Until the MCP server exists
  (roadmap item 3) the MCP signatures in a spec are a contract to be honoured later, not something to implement.

## 8. Data conventions

- EF Core + Npgsql, code-first migrations in `Xerp.Infrastructure`. Migrations are applied at API startup
  (acceptable while there is one instance; revisit before production).
- Money and quantities are `decimal` / `numeric`, never floating point.
- Audit columns on every tenant-owned row: `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`.
- Ledger tables (stock ledger, journal lines; later) are append-only: no `UPDATE`, no `DELETE`; corrections are
  reversing entries (ADR-0007).

## 9. Testing (ADR-0006)

- xUnit. Test-first: the builder writes tests from a spec's acceptance criteria before the code.
- Unit tests: Domain/Application rules and the architecture dependency test. No I/O.
- Integration tests: start the real API in-process (`WebApplicationFactory`) against a PostgreSQL started by
  Testcontainers (`postgres:18`), apply the real migrations, drive it over HTTP. Never the dev database, never
  an in-memory or SQLite provider (query filters, unique indexes and collation behaviour must be the real ones).
- Tests must be independent: each creates its own tenants (unique codes), so tests can share one database
  container and run in parallel.
- Each acceptance criterion maps to at least one named test. Each feature has a tenant-isolation test.

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
