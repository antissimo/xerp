# Spec 001 — Foundation (layers, tenants, API keys, errors, tests) + units of measure

Status: ready for implementation. Branch: `feat/001-foundation-uom`.
Read first: `docs/architecture.md`, ADR-0001 … ADR-0006. Why this is the first spec: `docs/roadmap.md` section 2.

## 1. Goal

Turn the single-project scaffold into the layered, tenant-scoped, authenticated, tested solution that
`CLAUDE.md` demands, and prove every part of it with one real resource going through all layers:
**units of measure** (UoM). After this spec:

- the solution has the four layers and two test projects, and `dotnet build` / `dotnet test` are green;
- a platform admin can create a tenant and receives its first API key;
- a tenant API key can manage that tenant's units of measure and nothing else;
- all errors follow the error model;
- no un-tenanted endpoint or table remains.

## 2. Scope

In scope
1. Solution restructure per `docs/architecture.md` section 2 (`src/Xerp.Domain`, `src/Xerp.Application`,
   `src/Xerp.Infrastructure`, `src/Xerp.Api`, `tests/Xerp.UnitTests`, `tests/Xerp.IntegrationTests`, one
   solution file at the repository root). The `api/` directory is removed.
2. Tenants, API keys, authentication, tenant context, tenant filtering and stamping.
3. Error model (`docs/architecture.md` section 6).
4. Units of measure: list, get by id, get by code, create, replace, delete.
5. `GET /api/v1/whoami`.
6. Test infrastructure (xUnit, `WebApplicationFactory`, Testcontainers PostgreSQL 18) and the wrapper script
   `scripts/dotnet.sh` that runs `dotnet <args>` inside `mcr.microsoft.com/dotnet/sdk:10.0`.
7. `compose.yaml`, Dockerfile and `.env.example` updated to the new layout and the admin key setting.

Removed by this spec (they return in specs 002 and 004)
- Entities, tables and endpoints for Article, Partner, Warehouse; the generic `CrudController`; the old
  root-level routes (`/articles`, `/partners`, `/warehouses`, `/units-of-measure`).
- The existing `Initial` migration. There is no deployed database: delete it and create one new initial
  migration in `Xerp.Infrastructure` containing exactly the tables of this spec. (A local dev volume must be
  dropped with `docker compose down -v`.)

Out of scope
- MCP server and CLI (the MCP signatures in section 5 are a contract for spec 003, **not** to be implemented now).
- Permissions/roles, API key management endpoints (list/create/revoke), tenant update/deactivate endpoints.
- UoM conversions, UoM categories, rounding precision (spec 009).
- Audit log, optimistic concurrency, rate limiting, row-level security.

## 3. Data

`Tenant` (not tenant-owned): `Id` uuid v7, `Code`, `Name`, `IsActive` (default true), `CreatedAt`.

`ApiKey` (tenant-owned): `Id` uuid v7, `TenantId`, `Name` (1–100), `ActorType` (`human` | `agent`),
`KeyHash` (lower-case hex SHA-256 of the full key string, unique), `IsActive` (default true), `CreatedAt`.

`UnitOfMeasure` (tenant-owned): `Id` uuid v7, `TenantId`, `Code`, `Name`, `IsActive`, `CreatedAt`, `UpdatedAt`,
`CreatedBy`, `UpdatedBy` (API key ids).

Indexes: unique `Tenant.Code` (case-insensitive); unique `ApiKey.KeyHash`; unique
`(UnitOfMeasure.TenantId, UnitOfMeasure.Code)` (case-insensitive on `Code`).
How case-insensitivity is implemented (normalised column, `citext`, expression index) is the builder's choice,
but it must be enforced by the database, not only by a prior check.

## 4. Operations — HTTP

All routes are under `/api/v1`. All request and response bodies are JSON with camelCase names.
Tenant routes require `Authorization: Bearer <tenant API key>`. Admin routes require
`Authorization: Bearer <admin key>`.

### 4.1 Create tenant — `POST /api/v1/admin/tenants`

Request: `{ "code": string, "name": string }`

`201 Created`:
```json
{
  "tenant": { "id": "uuid", "code": "acme", "name": "Acme d.o.o.", "isActive": true, "createdAt": "2026-10-08T12:00:00Z" },
  "apiKey": { "id": "uuid", "name": "initial", "actorType": "human", "key": "xerp_…" }
}
```
Errors: `400 VALIDATION_FAILED`, `401 UNAUTHENTICATED`, `403 FORBIDDEN`, `409 CODE_TAKEN`.

### 4.2 Who am I — `GET /api/v1/whoami`

`200 OK`:
```json
{ "tenant": { "id": "uuid", "code": "acme", "name": "Acme d.o.o." },
  "actor":  { "apiKeyId": "uuid", "name": "initial", "actorType": "human" } }
```
Errors: `401`, `403`.

### 4.3 Units of measure — `/api/v1/units-of-measure`

Representation (`UnitOfMeasure`):
```json
{ "id": "uuid", "code": "kg", "name": "Kilogram", "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /units-of-measure?search=&isActive=&limit=&offset=` | `200` `{ "items": [UnitOfMeasure], "total": int, "limit": int, "offset": int }` | 400, 401, 403 |
| Get | `GET /units-of-measure/{id}` | `200` UnitOfMeasure | 401, 403, 404 |
| Get by code | `GET /units-of-measure/by-code/{code}` | `200` UnitOfMeasure | 401, 403, 404 |
| Create | `POST /units-of-measure` body `{ "code", "name", "isActive"? }` | `201` UnitOfMeasure, `Location: /api/v1/units-of-measure/{id}` | 400, 401, 403, 409 `CODE_TAKEN` |
| Replace | `PUT /units-of-measure/{id}` body `{ "code", "name", "isActive" }` | `200` UnitOfMeasure | 400, 401, 403, 404, 409 `CODE_TAKEN` |
| Delete | `DELETE /units-of-measure/{id}` | `204`, empty body | 401, 403, 404 |

### 4.4 Health — `GET /health`
Unchanged, unauthenticated: `200 { "status": "ok", "db": "ok" }` when the database is reachable.

## 5. Operations — MCP (contract only; implemented in spec 003)

One tool per Application operation, same field names, limits and error codes as HTTP.
Errors are tool errors (`isError: true`) whose structured content is `{ "code", "detail", "errors"? }`.

| Tool | Input | Output |
|---|---|---|
| `whoami` | `{}` | as 4.2 |
| `uom_list` | `{ search?: string, isActive?: boolean, limit?: int, offset?: int }` | `{ items, total, limit, offset }` |
| `uom_get` | `{ id?: uuid, code?: string }` — exactly one of the two | UnitOfMeasure |
| `uom_create` | `{ code: string, name: string, isActive?: boolean }` | UnitOfMeasure |
| `uom_update` | `{ id: uuid, code: string, name: string, isActive: boolean }` | UnitOfMeasure |
| `uom_delete` | `{ id: uuid }` | `{ deleted: true }` |

Tenant provisioning has no MCP tool. What this means for spec 001: every operation in 4.2–4.3 must exist as an
Application-layer operation that takes plain inputs and an `ITenantContext`-style abstraction and returns a
result or an `AppError`, with no ASP.NET Core types — so spec 003 can call it unchanged.

## 6. Business rules

Codes (tenant code and UoM code)
- R1. Leading/trailing whitespace is trimmed before validation and storage.
- R2. After trimming: length 1–50, and every character is a Unicode letter, a Unicode number, or one of
  `.` `_` `-` (regex `^[\p{L}\p{N}._-]{1,50}$`).
- R3. Stored exactly as entered (after trim). Uniqueness and lookup are case-insensitive: `kg` and `KG` are the
  same code. Tenant codes are unique across the platform; UoM codes are unique within a tenant.

Names
- R4. Trimmed; 1–200 characters after trimming (tenant name and UoM name).

Units of measure
- R5. `isActive` is optional on create (default `true`) and **required** on replace (all three fields are required
  on `PUT`; an omitted `isActive` is a validation error, not a silent re-activation).
- R6. Replace may change the code. A unit keeps its own code without conflict, including a change of letter case
  only.
- R7. `createdAt` = `updatedAt` on create. Replace sets `updatedAt` to now and `updatedBy` to the acting key;
  `createdAt` and `createdBy` never change.
- R8. Delete removes the row (nothing references a unit yet; spec 002 introduces `IN_USE`).
- R9. List: `search` is trimmed; empty means no filter; max 100 characters; matches case-insensitively as a
  substring of `code` or `name`; `%`, `_` and `\` in `search` are literal characters, not wildcards.
  `isActive` filters when present. Ordering: by code, case-insensitive, ascending. `total` is the number of rows
  matching the filters before paging. `limit` default 50, allowed 1–500. `offset` default 0, allowed >= 0.
  The response echoes the applied `limit` and `offset`.

Requests
- R10. A body with an unknown property (including `id`, `tenantId`, `createdAt`, `createdBy`) is rejected.
  Clients cannot set ids, tenant or audit fields.
- R11. Ids are generated by the server as UUID v7.

Tenant provisioning and keys
- R12. Creating a tenant creates, in the same transaction, one API key named `initial` with `actorType: "human"`.
- R13. Key format: `xerp_` followed by 43 base64url characters (32 random bytes from a cryptographic RNG,
  unpadded). The plaintext key appears only in the `201` response of tenant creation.

## 7. Edge cases

- E1. `code` or `name` missing, `null`, empty or whitespace-only -> `400`, `errors` has that field's key.
- E2. Code of 51 characters, name of 201 characters -> `400`. Exactly 50 / 200 -> accepted.
- E3. Code containing a space, `/`, `%` or `?` -> `400` with `errors.code`.
- E4. Malformed JSON, missing body, wrong JSON type for a field (e.g. `"name": 123`) -> `400 VALIDATION_FAILED`
  with a non-empty `errors` object (key not specified by this spec).
- E5. `limit` = 0, 501, -1 or `abc` -> `400` with `errors.limit`; `offset` = -1 -> `400` with `errors.offset`;
  `isActive=maybe` -> `400` with `errors.isActive`; `search` of 101 characters -> `400` with `errors.search`.
- E6. `{id}` that is not a UUID, a well-formed UUID that does not exist, and a `{code}` that does not exist or
  is not a valid code -> `404 NOT_FOUND` with a problem body.
- E7. Creating `KG` when `kg` exists in the same tenant -> `409 CODE_TAKEN`. `GET by-code/KG` returns the unit
  stored as `kg`, with `"code": "kg"`.
- E8. Concurrent creates of the same code in one tenant: exactly one succeeds, the others get
  `409 CODE_TAKEN`; none gets `500`.
- E9. Offset beyond the last row -> `200` with empty `items` and the correct `total`.
- E10. Deleting twice: second call -> `404`.

## 8. Tenant isolation

- T1. The tenant is resolved only from the API key. No route, header, query or body field selects a tenant.
- T2. `Tenant` is the only entity without `TenantId`. `ApiKey` and `UnitOfMeasure` implement the tenant-owned
  marker and are covered by the global query filter, by stamping on insert and by the cross-tenant write check
  (`docs/architecture.md` section 3).
- T3. The only code allowed to bypass the filter is the API-key lookup during authentication (by `KeyHash`,
  before a tenant is known).
- T4. For a key of tenant B, any unit of tenant A is non-existent: not listed, not counted in `total`,
  `404 NOT_FOUND` on get / get-by-code / replace / delete, and it does not cause `CODE_TAKEN` in tenant B.

## 9. Security requirements

- S1. All `/api/v1` routes require authentication; `/health` does not. No other unauthenticated route exposes
  tenant data. (The OpenAPI document may stay, in the Development environment only.)
- S2. `Authorization` must be `Bearer <key>`. Missing header, another scheme, unknown key, key with
  `IsActive = false`, or key of a tenant with `IsActive = false` -> `401 UNAUTHENTICATED` with header
  `WWW-Authenticate: Bearer`.
- S3. Admin routes accept only the configured admin key (`Xerp:AdminKey`; environment `Xerp__AdminKey`;
  compose passes `${XERP_ADMIN_KEY}`), compared in constant time. If the setting is absent, empty or shorter
  than 32 characters, the admin key is treated as not configured and every admin request gets `401`.
- S4. A valid tenant key on an admin route -> `403 FORBIDDEN`. The admin key on a tenant route -> `403 FORBIDDEN`.
- S5. API keys are stored only as SHA-256 hashes. Plaintext keys and the admin key are never written to logs or
  to the database.
- S6. Error bodies never contain stack traces, SQL, connection strings or other tenants' data. Unexpected
  failures -> `500 INTERNAL_ERROR`.
- S7. All database access is parameterised (EF Core LINQ); `search` input is escaped for `LIKE`/`ILIKE`.
- S8. The database port stays unpublished in `compose.yaml`.

## 10. Acceptance criteria

Each criterion maps to at least one named test unless marked *(manual)*. "Problem with code X" means: response
`Content-Type` is `application/problem+json` and the JSON body has `"code": "X"` and a `status` equal to the HTTP
status. Tests assert status and `code` (and `errors` keys where stated), never message text.
In integration tests the admin key is supplied through test configuration; each test creates its own tenant(s)
with unique codes via 4.1.

Structure and build
- AC-01 *(manual)* From a clean checkout, `scripts/dotnet.sh build` and `scripts/dotnet.sh test` exit with code 0;
  afterwards `git status` shows no untracked files outside ignored paths and the worktree contains no files
  owned by root. The builder's summary states the exact commands run.
- AC-02 The repository contains exactly one solution file at the root, the six projects of section 2, and no
  `api/` directory. *(manual, by inspection)*
- AC-03 (unit) The `Xerp.Domain` assembly references no assembly whose name starts with
  `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Npgsql` or `Xerp.`.
- AC-04 (unit) The `Xerp.Application` assembly references no assembly whose name starts with
  `Microsoft.AspNetCore` or `Npgsql`, nor `Xerp.Infrastructure` or `Xerp.Api`.
- AC-05 (unit) The `Xerp.Infrastructure` assembly does not reference `Xerp.Api`.
- AC-06 (integration) In the EF Core model, every entity type except `Tenant` has a `TenantId` property and a
  global query filter.
- AC-07 (integration) `GET /articles`, `GET /partners`, `GET /warehouses` and `GET /units-of-measure`
  (old root routes) return `404`.
- AC-08 *(manual)* `docker compose up --build` with `XERP_ADMIN_KEY` set starts the API; `GET /health` returns
  `200`; creating a tenant and a unit with `curl` works. The builder's summary shows the commands and responses.

Authentication and tenants
- AC-10 `GET /health` without credentials -> `200`.
- AC-11 `POST /api/v1/admin/tenants` with the admin key and `{ "code": "<unique>", "name": "Acme" }` -> `201`;
  body has `tenant.id` (UUID), `tenant.code`, `tenant.name`, `tenant.isActive == true`, `tenant.createdAt`,
  `apiKey.id` (UUID), `apiKey.name == "initial"`, `apiKey.actorType == "human"`, and `apiKey.key` matching
  `^xerp_[A-Za-z0-9_-]{43}$`.
- AC-12 Two tenants created in a row receive different keys.
- AC-13 `GET /api/v1/whoami` with the key from AC-11 -> `200`; `tenant.id`, `tenant.code`, `tenant.name` equal
  the created tenant; `actor.apiKeyId == apiKey.id`; `actor.name == "initial"`; `actor.actorType == "human"`.
- AC-14 Creating a tenant whose code differs from an existing one only by letter case -> `409`, problem with
  code `CODE_TAKEN`.
- AC-15 Creating a tenant with a missing `code`, with a code containing a space, with a 51-character code, or
  with a whitespace-only `name` -> `400`, problem with code `VALIDATION_FAILED`, `errors` containing the key
  `code` (first three cases) or `name` (last case).
- AC-16 `POST /api/v1/admin/tenants` with no `Authorization` header, with `Authorization: Bearer wrong`, and
  with `Authorization: Basic …` -> `401`, problem with code `UNAUTHENTICATED`, response header
  `WWW-Authenticate` starting with `Bearer`.
- AC-17 `POST /api/v1/admin/tenants` with a valid tenant API key -> `403`, problem with code `FORBIDDEN`.
- AC-18 `GET /api/v1/whoami` and `GET /api/v1/units-of-measure` with the admin key -> `403`, problem with code
  `FORBIDDEN`.
- AC-19 `GET /api/v1/units-of-measure` and `GET /api/v1/whoami` with no header, with an unknown key of valid
  format, and with a key string that is not of valid format -> `401`, problem with code `UNAUTHENTICATED`,
  `WWW-Authenticate` starting with `Bearer`.
- AC-20 After the test sets `IsActive = false` on the key's `ApiKey` row directly in the database, a request
  with that key -> `401 UNAUTHENTICATED`. Likewise after setting `IsActive = false` on the `Tenant` row.
- AC-21 With an API host configured with no admin key, and with one configured with a 31-character admin key,
  `POST /api/v1/admin/tenants` using that value (or an empty bearer token) -> `401 UNAUTHENTICATED`.
- AC-22 After AC-11, no column of the tenant's `ApiKey` row contains the plaintext key, and `KeyHash` equals the
  lower-case hex SHA-256 of the UTF-8 bytes of the key string.

Units of measure — behaviour
- AC-30 `POST` `{ "code": "kg", "name": "Kilogram" }` -> `201`; body has a UUID `id`, `code == "kg"`,
  `name == "Kilogram"`, `isActive == true`, `createdAt == updatedAt`, `createdBy == updatedBy ==` the acting
  key's id; the `Location` header ends with `/api/v1/units-of-measure/{id}`; the body contains no `tenantId`.
- AC-31 `GET /{id}` of that unit -> `200` with the same representation. `GET /by-code/kg` and
  `GET /by-code/KG` -> `200`, same `id`, `code == "kg"`.
- AC-32 `POST` `{ "code": "  pcs  ", "name": "  Piece  " }` -> `201` with `code == "pcs"`, `name == "Piece"`.
- AC-33 `POST` with `"isActive": false` -> `201` with `isActive == false`.
- AC-34 `POST` with codes `m²`, `kom.`, `box-10`, `l_1` -> `201` each.
- AC-35 `POST` with a 50-character code and a 200-character name -> `201`. With a 51-character code -> `400`,
  `errors` has key `code`. With a 201-character name -> `400`, `errors` has key `name`.
- AC-36 `POST` with `code` missing, `null`, `""`, `"   "`, `"a b"`, `"a/b"`, `"a%"`, `"a?"` -> `400`
  `VALIDATION_FAILED`, `errors` has key `code`. `POST` with `name` missing, `null`, `""`, `"   "` -> `400`,
  `errors` has key `name`. A body failing both -> `errors` has both keys.
- AC-37 `POST` with an extra property (`"tenantId"`, `"id"` or `"foo"`), with malformed JSON, with an empty
  body, or with `"name": 123` -> `400`, problem with code `VALIDATION_FAILED`, non-empty `errors`; no row is
  created.
- AC-38 `POST` of `kg` twice, and of `KG` after `kg`, in the same tenant -> second response `409`, problem with
  code `CODE_TAKEN`.
- AC-39 Ten parallel `POST`s of the same new code in one tenant -> exactly one `201`, nine `409 CODE_TAKEN`,
  no other status.
- AC-40 `PUT /{id}` `{ "code": "kgm", "name": "Kilogramme", "isActive": false }` -> `200` with the new values;
  `id`, `createdAt`, `createdBy` unchanged; `updatedAt` >= the previous `updatedAt`; a following `GET /{id}`
  returns the same; `GET /by-code/kg` -> `404`.
- AC-41 `PUT` without `isActive`, without `code`, or without `name` -> `400` `VALIDATION_FAILED` with `errors`
  key `isActive`, `code`, `name` respectively; the unit is unchanged.
- AC-42 `PUT` keeping the unit's own code -> `200`; `PUT` changing only the letter case of its own code
  (`kg` -> `KG`) -> `200` with `code == "KG"`.
- AC-43 `PUT` setting the code to that of another unit of the same tenant (any letter case) -> `409`
  `CODE_TAKEN`; the unit is unchanged.
- AC-44 `PUT` and `GET` and `DELETE` with a random UUID -> `404` problem with code `NOT_FOUND`.
  `GET /units-of-measure/not-a-uuid` -> `404` problem with code `NOT_FOUND`.
  `GET /by-code/nope` -> `404` problem with code `NOT_FOUND`.
- AC-45 `DELETE /{id}` -> `204` with empty body; then `GET /{id}` -> `404`; second `DELETE` -> `404`;
  a new `POST` with the same code -> `201`.
- AC-46 When a second key of the same tenant (inserted by the test directly in the database) replaces a unit,
  `updatedBy` is the second key's id and `createdBy` is still the first key's id.

Units of measure — list
- AC-50 In a new tenant, `GET /units-of-measure` -> `200` `{ "items": [], "total": 0, "limit": 50, "offset": 0 }`.
- AC-51 With units `b`, `a`, `c` created in that order, list returns codes `a`, `b`, `c` and `total == 3`.
- AC-52 With 3 units: `limit=2` -> 2 items, `total == 3`, `limit == 2`, `offset == 0`; `limit=2&offset=2` ->
  1 item (the third by code); `offset=10` -> 0 items, `total == 3`.
- AC-53 `limit=1` and `limit=500` -> `200`. `limit=0`, `limit=501`, `limit=-1`, `limit=abc` -> `400`
  `VALIDATION_FAILED` with `errors` key `limit`. `offset=-1` -> `400` with `errors` key `offset`.
- AC-54 `isActive=true` returns only active units, `isActive=false` only inactive, absent returns both;
  `total` reflects the filter. `isActive=maybe` -> `400` with `errors` key `isActive`.
- AC-55 With units (`kg`, `Kilogram`), (`g`, `Gram`), (`pcs`, `Piece`): `search=GRAM` returns `g` and `kg`;
  `search=pc` returns `pcs`; `search=%20kg%20` (spaces around) returns `kg`; `search=zzz` returns none with
  `total == 0`; `search=` (empty) returns all three.
- AC-56 With units (`cot`, `100% cotton`) and (`pln`, `Plain`): `search=%25` (a literal `%`) returns only the first;
  `search=_` returns neither.
- AC-57 A `search` of 100 characters -> `200`; of 101 characters -> `400` with `errors` key `search`.

Tenant isolation (tenants A and B, each with its own key)
- AC-60 A creates `kg`. B's list -> `items` empty, `total == 0`.
- AC-61 B: `GET /{A's id}` -> `404 NOT_FOUND`; `GET /by-code/kg` -> `404 NOT_FOUND`.
- AC-62 B: `PUT /{A's id}` with a valid body -> `404 NOT_FOUND`; A then reads its unit unchanged.
- AC-63 B: `DELETE /{A's id}` -> `404 NOT_FOUND`; A can still read its unit.
- AC-64 B: `POST` `{ "code": "kg", … }` -> `201` (no `CODE_TAKEN` across tenants); the two units have different
  ids; each tenant's `by-code/kg` returns its own.
- AC-65 B's `search=k` and paging never return A's rows; B's `total` counts only B's rows.
- AC-66 `whoami` with A's key returns tenant A, with B's key tenant B.
- AC-67 (integration, below HTTP) Saving a `UnitOfMeasure` whose `TenantId` is tenant A through a DbContext
  whose current tenant is B fails (exception or error) and writes no row.

Errors
- AC-70 Every error response asserted in AC-14 … AC-67 has `Content-Type: application/problem+json` and a body
  with `code`, `status`, `title` and `detail`.
- AC-71 A request to an unknown path under `/api/v1` with a valid tenant key -> `404` problem with code
  `NOT_FOUND`.

## 11. Notes for the builder

- Existing behaviour worth keeping from the scaffold: UUID v7 ids, problem+json with `code`, list envelope.
  Behaviour that changes on purpose: paging values are rejected instead of clamped; codes are case-insensitive;
  unknown properties are rejected; `search` wildcards are escaped; routes move under `/api/v1`.
- Pre-checking a duplicate code gives a friendly path, but the unique index is the authority; AC-39 only passes
  if the database constraint violation is translated to `CODE_TAKEN` (in Infrastructure — Application must not
  reference Npgsql).
- The suggested docker invocation in `docs/architecture.md` section 10 is unverified. If it needs changes, make
  it work in `scripts/dotnet.sh` and report the final form in your summary; if Docker socket access is not
  possible at all, write `docs/questions/001-q.md` and do not substitute another database engine.
- Anything unclear or contradictory: `docs/questions/001-q.md`, then continue with the rest.
