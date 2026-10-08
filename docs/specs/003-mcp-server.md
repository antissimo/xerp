# Spec 003 — MCP server and API key management

Status: ready for implementation once specs 001 and 002 are merged to `main`. Branch: `feat/003-mcp-server`.
Read first: `docs/architecture.md` (sections 4, 6, 7), specs 001 and 002, ADR-0003, ADR-0005, **ADR-0009**,
**ADR-0010**. Why this is the third spec: `docs/roadmap.md` backlog item 3.

Everything specs 001 and 002 established applies unchanged unless this spec says otherwise. Rule, edge-case and
criterion numbers are local to this spec; "001/R9" means rule R9 of spec 001.

## 1. Goal

Give the product's primary user its interface. After this spec:

- an agent connects to `/mcp` with its own API key and can do, through tools, everything a client of the HTTP
  API can do with units of measure and articles — same rules, same field names, same error codes, because both
  call the same Application operations;
- a tenant can create further API keys (in particular `agent` keys), list them and revoke them, so that every
  agent is a separate, attributable actor;
- the pattern "one operation = one HTTP endpoint + one MCP tool" is fixed and tested, so that from spec 004 on
  every spec ships both.

## 2. Scope

In scope
1. MCP endpoint `/mcp` in `Xerp.Api`: Streamable HTTP, stateless, tools only (ADR-0009).
2. Tools for every existing tenant operation: `whoami`, `uom_*` (001 §5), `article_*` (002 §5).
3. API key management: Application operations, HTTP endpoints under `/api/v1/api-keys`, and the tools
   `api_key_list`, `api_key_get`, `api_key_revoke` (ADR-0010).
4. Mapping of results and errors to MCP tool results; a tool-list test; HTTP/MCP parity tests.

Out of scope
- `api_key_create` as a tool (ADR-0010, point 5) and any tool for tenant provisioning.
- MCP resources, prompts, sampling, elicitation, tasks, notifications, sessions, resumability.
- OAuth-based MCP authorization (ADR-0009); permissions per key, key expiry, rotation, rename (roadmap item 6).
- Rate limiting (roadmap, later). A stdio transport or a separate bridge process.
- The OpenAPI document (spec 003a).
- Any change to the behaviour of the HTTP operations of specs 001 and 002.

## 3. Data

`ApiKey` gains three nullable columns: `CreatedBy` (id of the key that created it; `null` for a tenant's
`initial` key), `RevokedAt`, `RevokedBy`. Existing columns (001 §3) are unchanged. Rows are never deleted.
`CreatedBy` and `RevokedBy` are foreign keys `(TenantId, CreatedBy)` / `(TenantId, RevokedBy)` ->
`ApiKey (TenantId, Id)`, `ON DELETE RESTRICT`, like every audit column (architecture §8).
Invariant: `IsActive = false` exactly when `RevokedAt` is set — except for rows deactivated outside the
application (001/AC-20 does this in a test), which the code must tolerate.

Migration: one new migration; earlier migrations are not edited. No other table changes.

## 4. Operations — HTTP (API keys)

All routes are under `/api/v1` and require a tenant API key.

Representation (`ApiKey`):
```json
{ "id": "uuid", "name": "claude-warehouse", "actorType": "agent", "isActive": true,
  "createdAt": "…Z", "createdBy": "uuid", "revokedAt": null, "revokedBy": null }
```
`createdBy`, `revokedAt` and `revokedBy` are always present (`null` when not applicable). The representation
never contains the key, its hash or a `tenantId`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /api-keys?search=&actorType=&isActive=&limit=&offset=` | `200` `{ "items": [ApiKey], "total": int, "limit": int, "offset": int }` | 400, 401, 403 |
| Get | `GET /api-keys/{id}` | `200` ApiKey | 401, 403, 404 |
| Create | `POST /api-keys` body `{ "name", "actorType" }` | `201` ApiKey **plus** `"key": "xerp_…"`, `Location: /api/v1/api-keys/{id}`, `Cache-Control: no-store` | 400, 401, 403 |
| Revoke | `POST /api-keys/{id}/revoke` (no body) | `200` ApiKey with `isActive: false` | 401, 403, 404, 409 `CANNOT_REVOKE_SELF` |

New error code (added to the registry in `docs/architecture.md` section 6):

| HTTP | `code` | When |
|---|---|---|
| 409 | `CANNOT_REVOKE_SELF` | The key to revoke is the key that authenticated the request. |

## 5. Operations — MCP

### 5.1 Endpoint

- `POST /mcp` — JSON-RPC 2.0 over MCP Streamable HTTP. Requires `Authorization: Bearer <tenant API key>` on
  every request, `initialize` included.
- The server negotiates MCP protocol revision `2025-06-18` or later (the first with `structuredContent` /
  `outputSchema`).
- `initialize` result: `serverInfo.name == "xerp"`, `serverInfo.version` = the API's version; capabilities
  contain `tools` and neither `resources` nor `prompts`; `instructions` is a short text for the agent that
  states at least: all data belongs to the tenant of the API key; call `whoami` to see tenant and actor;
  records are referenced by `id`, `code` is for lookup; errors are JSON `{code, detail, errors}` and the `code`
  is what to branch on.
- No `MCP-Session-Id` header is issued. `GET /mcp` and `DELETE /mcp` with a valid key -> `405`.

### 5.2 Result mapping (ADR-0009)

- Success: `isError` absent or `false`; `structuredContent` = the JSON object the corresponding HTTP endpoint
  returns in its body; `content` = exactly one `text` block whose text is that object serialised as JSON.
  Operations whose HTTP response has no body (`204`) return `{ "deleted": true }`.
- Application error: `isError: true`; `content` = exactly one `text` block whose text is the JSON object
  `{ "code": string, "detail": string, "errors"?: { field: [messages] } }`; no `structuredContent`.
  `code` and the keys of `errors` are identical to those of the HTTP problem document for the same input.
- JSON-RPC error (no tool result): unknown tool name, malformed JSON-RPC, unknown method.
- HTTP error (no JSON-RPC at all): authentication and `Origin` failures (section 9).

### 5.3 Tools

Arguments are a JSON object; the input schema of every tool is closed (`additionalProperties: false`).
"As HTTP" means: the same fields, types, defaults, limits and rules as the HTTP operation named.

| Tool | Arguments (required unless marked `?`) | Success `structuredContent` | HTTP operation | Error codes |
|---|---|---|---|---|
| `whoami` | `{}` | as 001 §4.2 | `GET /whoami` | — |
| `uom_list` | `{ search?, isActive?, limit?, offset? }` | list envelope of UnitOfMeasure | `GET /units-of-measure` | `VALIDATION_FAILED` |
| `uom_get` | `{ id?, code? }` — exactly one | UnitOfMeasure | `GET /units-of-measure/{id}` or `/by-code/{code}` | `VALIDATION_FAILED`, `NOT_FOUND` |
| `uom_create` | `{ code, name, isActive? }` | UnitOfMeasure | `POST /units-of-measure` | `VALIDATION_FAILED`, `CODE_TAKEN` |
| `uom_update` | `{ id, code, name, isActive }` | UnitOfMeasure | `PUT /units-of-measure/{id}` | `VALIDATION_FAILED`, `NOT_FOUND`, `CODE_TAKEN` |
| `uom_delete` | `{ id }` | `{ "deleted": true }` | `DELETE /units-of-measure/{id}` | `NOT_FOUND`, `IN_USE` |
| `article_list` | `{ search?, type?, baseUnitId?, isActive?, limit?, offset? }` | list envelope of Article | `GET /articles` | `VALIDATION_FAILED` |
| `article_get` | `{ id?, code? }` — exactly one | Article | `GET /articles/{id}` or `/by-code/{code}` | `VALIDATION_FAILED`, `NOT_FOUND` |
| `article_create` | `{ code, name, type, baseUnitId, description?, isActive? }` | Article | `POST /articles` | `VALIDATION_FAILED`, `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `CODE_TAKEN` |
| `article_update` | `{ id, code, name, description, type, baseUnitId, isActive }` | Article | `PUT /articles/{id}` | `VALIDATION_FAILED`, `NOT_FOUND`, `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `CODE_TAKEN` |
| `article_delete` | `{ id }` | `{ "deleted": true }` | `DELETE /articles/{id}` | `NOT_FOUND` |
| `api_key_list` | `{ search?, actorType?, isActive?, limit?, offset? }` | list envelope of ApiKey | `GET /api-keys` | `VALIDATION_FAILED` |
| `api_key_get` | `{ id }` | ApiKey | `GET /api-keys/{id}` | `NOT_FOUND` |
| `api_key_revoke` | `{ id }` | ApiKey | `POST /api-keys/{id}/revoke` | `NOT_FOUND`, `CANNOT_REVOKE_SELF` |

These 14 tools are the complete list. Any tool can additionally return `INTERNAL_ERROR`.

Tool metadata (returned by `tools/list`)
- `description`: what the operation does, in one or two sentences, plus the error codes it can return and what
  each means for the caller (e.g. for `uom_delete`: "`IN_USE`: articles use this unit; list them with
  `article_list` `baseUnitId`, or deactivate the unit with `uom_update` instead").
- `inputSchema`: `type: object`, every property with `type` and `description`, `required` as in the table,
  enums for `type` (`stock`, `service`) and `actorType` (`human`, `agent`), `additionalProperties: false`.
  `description` of `article_update` is `string | null`.
- `outputSchema`: describes the success object.
- `annotations`:

| Tools | `readOnlyHint` | `destructiveHint` | `idempotentHint` | `openWorldHint` |
|---|---|---|---|---|
| `whoami`, `*_list`, `*_get` | true | — | — | false |
| `*_create` | false | false | false | false |
| `*_update`, `api_key_revoke` | false | true | true | false |
| `*_delete` | false | true | false | false |

## 6. Business rules

API keys
- R1. `name`: trimmed; 1–100 characters after trimming; no control characters (001/R4). Names need not be
  unique.
- R2. `actorType`: required, exactly `"human"` or `"agent"`; fixed for the life of the key.
- R3. A created key has the format of 001/R13, is stored only as its SHA-256 hash (001/S5), is active, belongs
  to the caller's tenant and has `createdBy` = the acting key. The plaintext appears only in the `201` response
  of `POST /api-keys`.
- R4. Unknown body properties are rejected (001/R10) — including `key`, `isActive`, `tenantId`, `id`.
- R5. Revoke sets `isActive = false`, `revokedAt` = now, `revokedBy` = the acting key. It is permanent: no
  operation sets `isActive` back to `true`.
- R6. Revoking an already inactive key succeeds (`200`) and changes nothing: `revokedAt` and `revokedBy` keep
  their first values (or stay `null` for a row deactivated outside the application).
- R7. A key cannot revoke itself -> `409 CANNOT_REVOKE_SELF`. The check precedes R6.
- R8. Revocations are serialised per tenant so that a revocation only succeeds if the acting key is still
  active when it commits. Two keys revoking each other concurrently can therefore never both succeed; a tenant
  always retains at least one active key.
- R9. A revoked key is rejected from the next request on (`401 UNAUTHENTICATED`, 001/S2), on `/api/v1` and on
  `/mcp` alike. Records it created keep their `createdBy` / `updatedBy`.
- R9a. Query strings and bodies follow 001/R14–R16 on every API key route (unknown query parameters rejected
  under their own name; no input may cause `500`).
- R10. List: `search` as 001/R9 but matching `name` only; `actorType` and `isActive` filter when present;
  ordering by `createdAt` ascending, then `id`; `limit`, `offset`, `total` as 001/R9.
- R11. There is no interface restriction by `actorType`: a `human` key may use `/mcp`, an `agent` key may use
  `/api/v1` (ADR-0003: the label restricts nothing).

MCP
- R12. A tool binds its arguments, calls exactly one Application operation and maps the result (5.2). It
  contains no validation, no defaulting and no data access of its own. Every rule a tool call exhibits is a rule
  of the Application operation and is identical over HTTP.
- R13. `*_get` with `id` or `code`: exactly one of the two must be present and non-null; both or neither ->
  `VALIDATION_FAILED` with `errors` keys `id` and `code`. This rule lives in the Application layer.
- R14. Arguments use JSON types, not strings: `limit`/`offset` numbers, `isActive` boolean. A wrong JSON type,
  a missing required argument, an unknown argument or `arguments` that is not an object -> tool error
  `VALIDATION_FAILED` with non-empty `errors` (for a missing or rule-violating argument: the key is the
  argument's name, as over HTTP). It is never a JSON-RPC error and never `INTERNAL_ERROR`.
- R15. An `id` argument that addresses the record (`uom_get`, `*_update`, `*_delete`, `api_key_get`,
  `api_key_revoke`) and is a string but not a UUID -> `NOT_FOUND`, as a malformed `{id}` path segment over HTTP
  (001/E6). A reference argument (`baseUnitId`) that is not a UUID -> `VALIDATION_FAILED` (002/E3).
- R16. Writes through a tool are attributed exactly as over HTTP: `createdBy` / `updatedBy` / `revokedBy` are
  the id of the key that authenticated the MCP request.
- R17. An unexpected exception inside a tool -> tool error `INTERNAL_ERROR` with a generic `detail`; nothing of
  the exception reaches the client (001/S6).

## 7. Edge cases

- E1. `POST /api-keys` with `name` missing, `null`, `""`, whitespace-only or 101 characters -> `400` with
  `errors.name`; `actorType` missing, `null`, `"Agent"`, `"robot"` -> `400` with `errors.actorType`.
- E2. `GET /api-keys/{id}`, revoke: malformed id, unknown id, id of another tenant's key -> `404 NOT_FOUND`.
- E3. Revoking the tenant's `initial` key with another key is allowed.
- E4. Keys A and B of one tenant revoke each other at the same time: outcomes are (`200`, `401`) in either
  order — never (`200`, `200`), never `500`; afterwards exactly one of the two still authenticates.
- E5. A key is revoked between two MCP requests of the same agent: the second request gets HTTP `401`
  (there is no session that could keep it alive).
- E6. `tools/call` with a name not in 5.3 (e.g. `api_key_create`, `tenant_create`) -> JSON-RPC error, HTTP `200`
  or as the transport defines; not a tool result, not HTTP `500`.
- E7. `uom_get` with `{}` or with both `id` and `code` -> `VALIDATION_FAILED` (R13). `uom_get`
  `{ "code": "KG" }` returns the unit stored as `kg` (001/E7).
- E8. `uom_list` with `{ "limit": "10" }`, `{ "limit": 0 }`, `{ "limit": 501 }`, `{ "foo": 1 }` ->
  `VALIDATION_FAILED`; the first three with `errors.limit`.
- E9. `article_update` without `description` -> `VALIDATION_FAILED` with `errors.description`; with
  `"description": null` -> success (002/R11).
- E10. `uom_delete` of a unit in use -> tool error `IN_USE`; `article_create` with another tenant's unit id ->
  tool error `REFERENCE_NOT_FOUND` with `errors.baseUnitId`.
- E11. `POST /mcp` with a body that is not JSON-RPC -> a `4xx` response or a JSON-RPC error; never `500`.
- E12. `whoami` called with unexpected arguments (`{ "x": 1 }`) -> `VALIDATION_FAILED`.

## 8. Tenant isolation

- T1. On `/mcp` the tenant is resolved only from the API key of the HTTP request carrying the JSON-RPC message
  (001/T1). No tool has a tenant argument; a `tenantId` argument is an unknown argument (R14).
- T2. Because the server is stateless, no state of one request (tenant, actor, results) is available to
  another; tenant context is established per request exactly as for `/api/v1`.
- T3. Through tools, records of another tenant are non-existent in exactly the ways specs 001 §8 and 002 §8
  define for HTTP: not listed, not counted, `NOT_FOUND` when addressed, `REFERENCE_NOT_FOUND` when referenced.
- T4. `ApiKey` is tenant-owned (001/T2): a key of tenant B cannot list, read or revoke keys of tenant A
  (`404 NOT_FOUND` / not listed). A created key always belongs to the caller's tenant.
- T5. The only query that bypasses the tenant filter remains the authentication lookup (001/T3). Key
  management and the MCP layer add no `IgnoreQueryFilters()`.
- T6. Tool descriptions, schemas and `instructions` are static: `tools/list` and `initialize` return the same
  content for every tenant and contain no tenant data.

## 9. Security requirements

- S1. `/mcp` requires authentication on every request with the rules of 001/S2: missing header, other scheme,
  unknown, revoked or inactive key, inactive tenant -> HTTP `401` problem with code `UNAUTHENTICATED` and
  `WWW-Authenticate: Bearer`. The admin key -> HTTP `403` problem with code `FORBIDDEN` (001/S4). No MCP method,
  `initialize` and `tools/list` included, is reachable without a tenant key.
- S2. A request to `/mcp` with an `Origin` header whose value is not listed in `Xerp:Mcp:AllowedOrigins`
  (string array, default empty) -> HTTP `403` problem with code `FORBIDDEN`, regardless of credentials.
  Requests without `Origin` are not affected. No CORS response headers are emitted for `/mcp`.
- S3. The plaintext of a key is returned only in the body of `201 POST /api-keys` (and, since spec 001, of
  tenant creation). It never appears in any tool result, tool description, log line or other response.
  `KeyHash` is never returned anywhere. The `201` response carries `Cache-Control: no-store`.
- S4. API keys are never accepted in a URL (query string or path) on any route.
- S5. Logs may record tool name, key id, tenant id and resulting `code`; they never record the `Authorization`
  header or a plaintext key.
- S6. Tool errors never contain stack traces, SQL, constraint names or other tenants' data (001/S6, 002/S2).
- S7. Until permissions exist (roadmap item 6) every tenant key, of either actor type, may create keys over HTTP
  and revoke other keys. This is the trust level of ADR-0003 and is stated in the builder's summary as a known
  limitation, not hidden.
- S8. Known gap, not to be solved here: no rate limiting on `/mcp` or `/api/v1` (ADR-0009, consequences).

## 10. Acceptance criteria

Conventions as in spec 001 §10. Additionally:
- "MCP client" = the official C# SDK's client connected to `/mcp` of the in-process test server with a given
  API key. "Tool error X" = a tool result with `isError == true`, exactly one `text` content block whose text
  parses as a JSON object with `code == "X"` and a non-empty `detail`, and no `structuredContent`.
- "Tool success" = `isError` not `true`, a `structuredContent` object, and exactly one `text` content block
  whose text parses to JSON equal to `structuredContent`.
- "Equal to HTTP" = `structuredContent` is JSON-equal (same properties, same values) to the body of the named
  HTTP request made with the same key.
- Each test creates its own tenant(s); further keys are created with `POST /api/v1/api-keys`.

Structure
- AC-01 *(manual)* `scripts/dotnet.sh build` and `scripts/dotnet.sh test` exit with code 0 from a clean checkout;
  all tests of specs 001 and 002 pass and none was weakened, deleted or skipped. The builder's summary states
  the commands, the test counts, the SDK package version used and the MCP protocol revision negotiated.
- AC-02 *(manual, by inspection)* Exactly one migration was added; earlier migration files are unchanged.
- AC-03 (unit) No type in the namespace that holds the MCP tools (`Xerp.Api.Mcp` and below) has a constructor
  parameter, method parameter, field or property whose type is the DbContext, `IXerpDb` or any EF Core type.
- AC-04 (unit) The mapping from an `AppError` to a tool result yields a tool error with the same `code`,
  `detail` and `errors`; the mapping from an arbitrary exception yields `INTERNAL_ERROR` whose text does not
  contain the exception's message or type name.
- AC-05 (unit) The "exactly one of `id`, `code`" rule (R13) is tested on the Application operation, without MCP
  or HTTP: neither -> `VALIDATION_FAILED` with `errors` keys `id` and `code`; both -> the same.

API keys — HTTP
- AC-10 `POST /api-keys` `{ "name": "claude-warehouse", "actorType": "agent" }` -> `201`; body has a UUID `id`,
  `name`, `actorType == "agent"`, `isActive == true`, `createdAt`, `createdBy ==` the acting key's id,
  `revokedAt == null`, `revokedBy == null` (properties present), and `key` matching `^xerp_[A-Za-z0-9_-]{43}$`;
  `Location` ends with `/api/v1/api-keys/{id}`; `Cache-Control` contains `no-store`; no `tenantId`, no `keyHash`.
- AC-11 `GET /whoami` with the new key -> `200`, same tenant as the creating key, `actor.apiKeyId ==` the new
  id, `actor.name == "claude-warehouse"`, `actor.actorType == "agent"`.
- AC-12 After AC-10, the new `ApiKey` row's `KeyHash` equals the lower-case hex SHA-256 of the returned key and
  no column contains the plaintext (as 001/AC-22).
- AC-13 `POST /api-keys` with `"  bot  "` as name -> `201` with `name == "bot"`. Two keys with the same name ->
  both `201`, different ids, different `key` values.
- AC-14 `POST /api-keys` with `name` missing, `null`, `""`, `"   "`, or 101 characters -> `400`
  `VALIDATION_FAILED` with `errors` key `name`; a 100-character name -> `201`. With `actorType` missing, `null`,
  `"Agent"`, `"robot"` -> `400` with `errors` key `actorType`. With `name` = `"a\u0000b"` or `"a\nb"` -> `400`
  with `errors` key `name`. `GET /api-keys?foo=1` -> `400` with `errors` key `foo`.
- AC-15 `POST /api-keys` with an extra property (`"key"`, `"isActive"`, `"tenantId"`, `"foo"`) or malformed
  JSON -> `400` `VALIDATION_FAILED`, non-empty `errors`; the key list is unchanged.
- AC-16 `GET /api-keys/{id}` -> `200` with the representation of AC-10 without `key`. For the tenant's
  `initial` key: `createdBy == null`, `actorType == "human"`.
- AC-17 In a new tenant `GET /api-keys` -> `200`, one item (`initial`), `total == 1`, `limit == 50`,
  `offset == 0`. After creating `b-bot` (agent) then `a-bot` (human): items in creation order `initial`,
  `b-bot`, `a-bot`; no item has a `key` or `keyHash` property.
- AC-18 List filters: `actorType=agent` returns only agent keys, `actorType=robot` -> `400` with `errors` key
  `actorType`; `isActive=false` returns only revoked keys, `isActive=maybe` -> `400` with `errors` key
  `isActive`; `search=BOT` matches names case-insensitively; `total` reflects the filters; `limit=0`,
  `limit=501` -> `400` with `errors` key `limit`; `limit=1&offset=1` returns the second key.
- AC-19 Key A revokes key B: `POST /api-keys/{B}/revoke` -> `200` with `isActive == false`, a `revokedAt`, and
  `revokedBy == A`. A following `GET /whoami` with B's key -> `401` problem with code `UNAUTHENTICATED`.
  `GET /api-keys/{B}` with A -> `200`, still `isActive == false`.
- AC-20 Revoking B a second time -> `200`; `revokedAt` and `revokedBy` are identical to those of the first
  response.
- AC-21 `POST /api-keys/{own id}/revoke` -> `409`, problem with code `CANNOT_REVOKE_SELF`; the key still
  authenticates.
- AC-22 A unit of measure created with key B keeps `createdBy == B` after B is revoked.
- AC-23 Revoking the `initial` key with a second key -> `200`; the second key still works; the `initial` key
  gets `401`.
- AC-24 `GET /api-keys/{random UUID}`, `GET /api-keys/not-a-uuid`, `POST /api-keys/{random UUID}/revoke` ->
  `404` problem with code `NOT_FOUND`.
- AC-25 Race: in each of 20 rounds a tenant has active keys A and B; A's revoke of B and B's revoke of A are
  sent in parallel. In every round the two statuses are one `200` and one `401`, and afterwards exactly one of
  A and B gets `200` from `GET /whoami`.
- AC-26 `GET /api-keys`, `POST /api-keys`, `POST /api-keys/{id}/revoke` with no `Authorization` header -> `401`
  `UNAUTHENTICATED`; with the admin key -> `403` `FORBIDDEN`.

MCP — transport and authentication
- AC-30 An MCP client with a tenant key initialises successfully; the negotiated protocol revision is
  `>= "2025-06-18"`; `serverInfo.name == "xerp"`; capabilities include `tools` and include neither `resources`
  nor `prompts`; `instructions` is non-empty.
- AC-31 `POST /mcp` carrying a JSON-RPC `initialize` request, and one carrying `tools/list`, with no
  `Authorization` header, with `Authorization: Bearer wrong`, with `Authorization: Basic …`, and with a revoked
  key -> HTTP `401`, `Content-Type: application/problem+json`, `code == "UNAUTHENTICATED"`, `WWW-Authenticate`
  starting with `Bearer`.
- AC-32 The same requests with the admin key -> HTTP `403`, problem with code `FORBIDDEN`.
- AC-33 `POST /mcp` with a valid key and `Origin: https://evil.example` -> HTTP `403`, problem with code
  `FORBIDDEN`. With a host configured with `Xerp:Mcp:AllowedOrigins = ["https://app.example"]` and
  `Origin: https://app.example` -> the request is processed. No response from `/mcp` has an
  `Access-Control-Allow-Origin` header.
- AC-34 `GET /mcp` and `DELETE /mcp` with a valid key -> `405`. No response to `initialize` has an
  `MCP-Session-Id` header.
- AC-35 With an MCP client connected using key B, key B is revoked by key A over HTTP; the client's next
  `tools/call` fails with an HTTP `401` (surfaced by the client as an error, not as a tool result).
- AC-36 `POST /mcp` with a valid key and the body `not json` -> a response with status `< 500`.

MCP — tool list
- AC-40 `tools/list` returns exactly the 14 tool names of 5.3 — the test holds the expected list literally and
  fails on any missing or additional tool. In particular there is no `api_key_create` and no tenant tool.
- AC-41 Every listed tool has a non-empty `description`; an `inputSchema` with `type == "object"` and
  `additionalProperties == false`, whose `properties` and `required` are exactly those of 5.3; a `description`
  on every input property; and an `outputSchema`.
- AC-42 Every listed tool has the annotations of the table in 5.3.
- AC-43 In `inputSchema`, `article_create.type` and `article_list.type` are enums of exactly `stock`,
  `service`; `api_key_list.actorType` is an enum of exactly `human`, `agent`.
- AC-44 `tools/list` results for keys of two different tenants are JSON-equal.
- AC-45 `tools/call` with the name `api_key_create` and with the name `nope` -> a JSON-RPC error (the MCP client
  reports a protocol error); no tool result.

MCP — tools, success and parity
- AC-50 `whoami` -> tool success, equal to HTTP `GET /whoami`; with an `agent` key `actor.actorType == "agent"`.
- AC-51 `uom_create` `{ "code": "kg", "name": "Kilogram" }` -> tool success; `structuredContent` has a UUID
  `id`, `isActive == true`, `createdBy ==` the MCP key's id, and is equal to HTTP `GET /units-of-measure/{id}`.
- AC-52 `uom_get` `{ "id": … }`, `uom_get` `{ "code": "kg" }` and `uom_get` `{ "code": "KG" }` -> tool success,
  each equal to HTTP `GET /units-of-measure/{id}`.
- AC-53 `uom_list` `{}` -> tool success equal to HTTP `GET /units-of-measure`; `uom_list`
  `{ "search": "gram", "isActive": true, "limit": 1, "offset": 1 }` -> equal to HTTP with the same query.
- AC-54 `uom_update` `{ "id", "code": "kgm", "name": "Kilogramme", "isActive": false }` -> tool success with the
  new values; `updatedBy ==` the MCP key's id; equal to a following HTTP `GET`.
- AC-55 `uom_delete` `{ "id" }` of an unreferenced unit -> tool success with `structuredContent`
  `{ "deleted": true }`; HTTP `GET /units-of-measure/{id}` -> `404`.
- AC-56 `article_create` with a unit created over HTTP -> tool success; `baseUnit.id`, `baseUnit.code`,
  `baseUnit.name` are the unit's; `description == null`; equal to HTTP `GET /articles/{id}`.
- AC-57 `article_get` by `id` and by `code`; `article_list` `{}` and with
  `{ "type": "stock", "baseUnitId": U, "isActive": true, "search": "…" }` -> tool success, each equal to the
  corresponding HTTP request.
- AC-58 `article_update` with all seven arguments, `"description": null` -> tool success, `description == null`,
  equal to a following HTTP `GET`. `article_delete` -> `{ "deleted": true }`; HTTP `GET` -> `404`.
- AC-59 `api_key_list` `{}` and `api_key_list` `{ "actorType": "agent", "isActive": true }` -> tool success
  equal to the corresponding HTTP request; no item has `key` or `keyHash`. `api_key_get` `{ "id" }` -> equal to
  HTTP `GET /api-keys/{id}`.
- AC-60 `api_key_revoke` `{ "id": other key }` -> tool success, `isActive == false`, `revokedBy ==` the MCP
  key's id; that other key then gets `401` over HTTP.
- AC-61 A record created over HTTP by key H and updated through a tool by key M has `createdBy == H` and
  `updatedBy == M`.

MCP — tool errors
- AC-70 `uom_create` with `{ "name": "x" }` (no `code`) -> tool error `VALIDATION_FAILED` with `errors` key
  `code`; with `{ "code": "a b", "name": "" }` -> `errors` keys `code` and `name`. No unit is created.
- AC-71 `uom_create` with an unknown argument (`"tenantId"` or `"foo"`), with `"name": 123`, and `uom_list` with
  `{ "limit": "10" }` -> tool error `VALIDATION_FAILED` with non-empty `errors` — not a JSON-RPC error.
  `whoami` with `{ "x": 1 }` -> tool error `VALIDATION_FAILED`.
- AC-72 `uom_list` with `{ "limit": 0 }` and `{ "limit": 501 }` -> tool error `VALIDATION_FAILED` with `errors`
  key `limit`; `{ "offset": -1 }` -> `errors` key `offset`.
- AC-73 `uom_get` and `article_get` with `{}` and with both `id` and `code` -> tool error `VALIDATION_FAILED`
  with `errors` keys `id` and `code`.
- AC-74 `uom_get`, `uom_update` (valid body), `uom_delete`, `article_get`, `article_delete`, `api_key_get`,
  `api_key_revoke` with a random UUID, and `uom_get` with `{ "id": "not-a-uuid" }` and with
  `{ "code": "nope" }` -> tool error `NOT_FOUND`.
- AC-75 `uom_create` of an existing code in another letter case -> tool error `CODE_TAKEN`.
- AC-76 `uom_delete` of a unit used by an article -> tool error `IN_USE`; the unit still exists.
- AC-77 `article_create` with `baseUnitId` = a random UUID -> tool error `REFERENCE_NOT_FOUND` with `errors` key
  `baseUnitId`; with an inactive unit -> tool error `REFERENCE_INACTIVE` with `errors` key `baseUnitId`; with
  `"baseUnitId": "abc"` -> tool error `VALIDATION_FAILED` with `errors` key `baseUnitId`.
- AC-78 `article_update` without `description` -> tool error `VALIDATION_FAILED` with `errors` key
  `description`; the article is unchanged.
- AC-79 `api_key_revoke` with the MCP key's own id -> tool error `CANNOT_REVOKE_SELF`; the client's next
  `whoami` succeeds.
- AC-80 For each of AC-70, AC-75, AC-76 and AC-77 (first case), the same input sent to the HTTP endpoint yields
  a problem document with the same `code` and the same set of `errors` keys as the tool error.

Tenant isolation (tenants A and B, each with its own key and its own MCP client)
- AC-90 A creates unit `kg` and article `X1` through tools. B's `uom_list` and `article_list` -> empty,
  `total == 0`; B's `whoami` returns tenant B.
- AC-91 B: `uom_get` and `article_get` with A's ids and with A's codes; `uom_update`, `uom_delete`,
  `article_update`, `article_delete` with A's ids -> tool error `NOT_FOUND`. A reads both records unchanged.
- AC-92 B: `article_create` with `baseUnitId` = A's unit id -> tool error `REFERENCE_NOT_FOUND`;
  `article_list` `{ "baseUnitId": A's unit id }` -> empty.
- AC-93 B: `uom_create` `kg` and `article_create` `X1` -> tool success (no `CODE_TAKEN` across tenants).
- AC-94 B: `uom_create` with an argument `"tenantId": <A's tenant id>` -> tool error `VALIDATION_FAILED`;
  nothing is created in either tenant.
- AC-95 B over HTTP: `GET /api-keys` lists only B's keys; `GET /api-keys/{A's key id}` and
  `POST /api-keys/{A's key id}/revoke` -> `404 NOT_FOUND`; A's key still works. B through tools:
  `api_key_list` lists only B's keys; `api_key_get` and `api_key_revoke` with A's key id -> tool error
  `NOT_FOUND`.
- AC-96 A key created by B belongs to B: `GET /whoami` with it returns tenant B, and A's `GET /api-keys` does
  not list it.
- AC-97 Two MCP clients, one per tenant, issuing 20 interleaved `uom_create` / `uom_list` calls in parallel:
  every `uom_list` result contains only codes created by its own tenant.

Errors
- AC-98 Every HTTP error response asserted above has `Content-Type: application/problem+json` and a body with
  `code`, `status`, `title` and `detail`. No asserted response has status `500` and no tool result has code
  `INTERNAL_ERROR`.

## 11. Notes for the builder

- Start from `main` after specs 001 and 002 are merged.
- ADR-0009 lists what the architect verified in the MCP specification and what was **not** verified about the
  C# SDK. Expected, unverified: packages `ModelContextProtocol.AspNetCore` (server) and `ModelContextProtocol`
  (client for tests); an option of the HTTP transport that disables sessions (stateless); the ability to return
  a complete tool result (content, `structuredContent`, `isError`) from a tool. If the SDK cannot do something
  this spec requires — in particular closed input schemas with tool-error validation (R14), no session header,
  `405` on `GET`, or an error result without `structuredContent` for a tool that has an `outputSchema` — write
  `docs/questions/003-q.md` with what you found and continue with the rest; do not silently change the contract.
- R14 most likely means a tool must not let the SDK bind typed parameters and throw; bind leniently (raw JSON)
  and let the Application operation validate. Whatever the mechanism, the tests of AC-71 decide.
- Input and output schemas should be derived from the same DTOs the HTTP endpoints use, so that they cannot
  drift; hand-written descriptions stay next to the tool.
- The "exactly one of id/code" rule (R13) and the revoke rules (R5–R8) are Application code. AC-25 needs a
  database-level guarantee (row locks taken in a fixed order, or a serialisable transaction with the conflict
  translated to the loser being rejected); a check-then-write in memory will not pass.
- HTTP and MCP parity tests are the template for all later specs: keep the helper that compares a tool's
  `structuredContent` with an HTTP body reusable.
- Report in the summary: SDK version, protocol revision, and a transcript of a real MCP client (for example
  `claude mcp add --transport http xerp http://localhost:8000/mcp --header "Authorization: Bearer …"`, or the
  MCP inspector) listing the tools against `docker compose up` *(manual)*.
- Anything unclear or contradictory: `docs/questions/003-q.md`, then continue with the rest.
