# Spec 004 — Partners and warehouses

Status: ready for the tester and the builder once specs 001, 002 and 003 are merged to `main`.
Branches: `tests/004-partners-warehouses` (tester), `feat/004-partners-warehouses` (builder).
Read first: `docs/architecture.md` (sections 5, 7, 9), specs 001, 002 and 003, ADR-0008, ADR-0009,
**ADR-0011**. Why this is the fourth spec: `docs/roadmap.md` backlog item 4.

Everything specs 001–003 established applies unchanged unless this spec says otherwise: authentication, tenant
context, error model, list envelope, code and name rules, query-string and body rules, audit fields, MCP result
mapping, test infrastructure. Rule, edge-case and criterion numbers are local to this spec; "001/R9" means rule
R9 of spec 001. The OpenAPI document (spec 018) is not a prerequisite.

## 1. Goal

Complete the masters every later document needs. After this spec:

- a tenant key can list, read, create, replace and delete its tenant's **partners** — the companies and people
  it sells to and buys from — with roles (`isCustomer`, `isSupplier`), an optional tax id and an address;
- a tenant key can do the same with its **warehouses** — the places where stock will be kept (spec 005);
- every one of these operations exists as an HTTP endpoint **and** an MCP tool, built together for the first
  time (architecture §7), with a parity test;
- the acceptance tests are, for the first time, written by the tester from the criteria of this spec.

## 2. Scope

In scope
1. `Partner` and `Warehouse` entities, tables, one migration; Application operations.
2. HTTP endpoints under `/api/v1/partners` and `/api/v1/warehouses`.
3. MCP tools `partner_list`, `partner_get`, `partner_create`, `partner_update`, `partner_delete`,
   `warehouse_list`, `warehouse_get`, `warehouse_create`, `warehouse_update`, `warehouse_delete`, with metadata
   (description, schemas, annotations) as spec 003 §5.3 defines it.
4. The shared address fields and the "optional text" rule (ADR-0011), implemented once.

Out of scope
- Anything that references a partner or a warehouse: stock (005), orders (009, 010), invoices (014), payments
  (015). Until then both can always be deleted.
- Several addresses per partner, contact persons, e-mail, phone, bank accounts, payment terms, credit limits,
  currencies, price lists, partner groups, posting configuration.
- Validation of tax ids or addresses against any country's rules; a list of countries (ADR-0011).
- Bins/locations inside a warehouse, warehouse types, a default warehouse (ADR-0007; roadmap "Later").
- Any change to the behaviour of existing operations and tools. The only existing test that changes is the
  tool-list test of 003/AC-40 (section 5.3).

## 3. Data

`Partner` (tenant-owned): `Id` uuid v7, `TenantId`, `Code`, `Name`, `IsCustomer`, `IsSupplier`,
`TaxId` (nullable, max 50), the address columns, `IsActive`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`.

`Warehouse` (tenant-owned): `Id` uuid v7, `TenantId`, `Code`, `Name`, the address columns, `IsActive`,
`CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`.

Address columns (both tables, all nullable): `AddressLine1` (max 200), `AddressLine2` (max 200),
`PostalCode` (max 20), `City` (max 100), `Region` (max 100), `CountryCode` (2).

Indexes and keys
- Unique `(TenantId, Code)` per table, case-insensitive on `Code`, enforced by the database (as 001 §3).
- Unique key `(TenantId, Id)` on both tables, so that later specs can reference them with tenant-inclusive
  foreign keys (architecture §3, rule 4).
- `CreatedBy` / `UpdatedBy` are foreign keys `(TenantId, …)` -> `ApiKey (TenantId, Id)`, `ON DELETE RESTRICT`
  (architecture §8).
- No index on `TaxId` is required; it is not unique (ADR-0011, decision 6).

Migration: one new migration; earlier migrations are not edited.

## 4. Operations — HTTP

All routes are under `/api/v1` and require `Authorization: Bearer <tenant API key>`.

### 4.1 Partners

Representation (`Partner`):
```json
{ "id": "uuid", "code": "P-001", "name": "Acme d.o.o.",
  "isCustomer": true, "isSupplier": false, "taxId": null,
  "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "region": null,
  "countryCode": null,
  "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```
All properties are always present; optional text is `null` when empty. No `tenantId`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /partners?search=&isCustomer=&isSupplier=&isActive=&limit=&offset=` | `200` `{ "items": [Partner], "total": int, "limit": int, "offset": int }` | 400, 401, 403 |
| Get | `GET /partners/{id}` | `200` Partner | 401, 403, 404 |
| Get by code | `GET /partners/by-code/{code}` | `200` Partner | 401, 403, 404 |
| Create | `POST /partners` body `{ "code", "name", "isCustomer"?, "isSupplier"?, "taxId"?, "addressLine1"?, "addressLine2"?, "postalCode"?, "city"?, "region"?, "countryCode"?, "isActive"? }` | `201` Partner, `Location: /api/v1/partners/{id}` | 400, 401, 403, 409 `CODE_TAKEN` |
| Replace | `PUT /partners/{id}` body with all twelve fields: `code`, `name`, `isCustomer`, `isSupplier`, `taxId`, `addressLine1`, `addressLine2`, `postalCode`, `city`, `region`, `countryCode`, `isActive` | `200` Partner | 400, 401, 403, 404, 409 `CODE_TAKEN` |
| Delete | `DELETE /partners/{id}` | `204`, empty body | 401, 403, 404 |

### 4.2 Warehouses

Representation (`Warehouse`):
```json
{ "id": "uuid", "code": "WH-1", "name": "Main warehouse",
  "addressLine1": null, "addressLine2": null, "postalCode": null, "city": null, "region": null,
  "countryCode": null,
  "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /warehouses?search=&isActive=&limit=&offset=` | `200` list envelope of Warehouse | 400, 401, 403 |
| Get | `GET /warehouses/{id}` | `200` Warehouse | 401, 403, 404 |
| Get by code | `GET /warehouses/by-code/{code}` | `200` Warehouse | 401, 403, 404 |
| Create | `POST /warehouses` body `{ "code", "name", "addressLine1"?, "addressLine2"?, "postalCode"?, "city"?, "region"?, "countryCode"?, "isActive"? }` | `201` Warehouse, `Location: /api/v1/warehouses/{id}` | 400, 401, 403, 409 `CODE_TAKEN` |
| Replace | `PUT /warehouses/{id}` body with all nine fields: `code`, `name`, `addressLine1`, `addressLine2`, `postalCode`, `city`, `region`, `countryCode`, `isActive` | `200` Warehouse | 400, 401, 403, 404, 409 `CODE_TAKEN` |
| Delete | `DELETE /warehouses/{id}` | `204`, empty body | 401, 403, 404 |

No new error codes.

## 5. Operations — MCP

Implemented in this spec. Endpoint, authentication, result mapping and argument rules are those of spec 003
(§5.1, §5.2, R12–R17); "as HTTP" means the same fields, types, defaults, limits and rules as the HTTP operation.

### 5.1 Tools

`Address?` stands for the six optional arguments `addressLine1?`, `addressLine2?`, `postalCode?`, `city?`,
`region?`, `countryCode?` (each `string | null`); `Address` for the same six as required arguments
(each `string | null`).

| Tool | Arguments (required unless marked `?`) | Success `structuredContent` | HTTP operation | Error codes |
|---|---|---|---|---|
| `partner_list` | `{ search?, isCustomer?, isSupplier?, isActive?, limit?, offset? }` | list envelope of Partner | `GET /partners` | `VALIDATION_FAILED` |
| `partner_get` | `{ id?, code? }` — exactly one | Partner | `GET /partners/{id}` or `/by-code/{code}` | `VALIDATION_FAILED`, `NOT_FOUND` |
| `partner_create` | `{ code, name, isCustomer?, isSupplier?, taxId?, Address?, isActive? }` | Partner | `POST /partners` | `VALIDATION_FAILED`, `CODE_TAKEN` |
| `partner_update` | `{ id, code, name, isCustomer, isSupplier, taxId, Address, isActive }` | Partner | `PUT /partners/{id}` | `VALIDATION_FAILED`, `NOT_FOUND`, `CODE_TAKEN` |
| `partner_delete` | `{ id }` | `{ "deleted": true }` | `DELETE /partners/{id}` | `NOT_FOUND` |
| `warehouse_list` | `{ search?, isActive?, limit?, offset? }` | list envelope of Warehouse | `GET /warehouses` | `VALIDATION_FAILED` |
| `warehouse_get` | `{ id?, code? }` — exactly one | Warehouse | `GET /warehouses/{id}` or `/by-code/{code}` | `VALIDATION_FAILED`, `NOT_FOUND` |
| `warehouse_create` | `{ code, name, Address?, isActive? }` | Warehouse | `POST /warehouses` | `VALIDATION_FAILED`, `CODE_TAKEN` |
| `warehouse_update` | `{ id, code, name, Address, isActive }` | Warehouse | `PUT /warehouses/{id}` | `VALIDATION_FAILED`, `NOT_FOUND`, `CODE_TAKEN` |
| `warehouse_delete` | `{ id }` | `{ "deleted": true }` | `DELETE /warehouses/{id}` | `NOT_FOUND` |

### 5.2 Tool metadata

As spec 003 §5.3: a `description` that names the error codes and what each means for the caller; a closed
`inputSchema` (`additionalProperties: false`) with a `description` on every property and `required` as in the
table; an `outputSchema`; annotations by the pattern table of 003 §5.3 (`*_list`/`*_get` read-only; `*_create`;
`*_update`; `*_delete`). Additionally:
- nullable text arguments have the schema type `string` or `null`;
- the description of `partner_create` states that at least one of `isCustomer`, `isSupplier` must be `true`,
  and that `taxId` is not unique (search first to avoid a duplicate partner);
- the description of the `countryCode` property states the form (two upper-case letters, ISO 3166-1 alpha-2).

### 5.3 The complete tool list

After this spec `tools/list` returns exactly 24 tools: the 14 of spec 003 §5.3 and the 10 above.
The tool-list test written for 003/AC-40 holds the list literally and therefore changes in this spec: the
tester replaces the expected list with the 24 names (AC-90). This is a change of a test because the contract
changed and is approved here; no other existing test may change.

## 6. Business rules

Common to both resources
- R1. `code` follows 001/R1–R3: trimmed; `^[\p{L}\p{N}._-]{1,50}$`; stored as entered; unique and looked up
  case-insensitively within the tenant's records **of the same resource**. Partner codes, warehouse codes,
  article codes and unit codes are four separate namespaces.
- R2. `name` follows 001/R4: trimmed; 1–200 characters; no control characters.
- R3. **Optional text** (ADR-0011, decision 7) — `taxId` and the five free-text address fields. The value is a
  JSON string or `null`. It is trimmed; omitted (on create), `null`, empty and whitespace-only all mean "no
  value", stored and returned as `null`. Otherwise: at most the field's maximum length after trimming, and no
  control characters (001/R4) — these are single-line fields; a line break is invalid. A violation gives `400`
  with the field's `errors` key.

  | Field | Maximum length |
  |---|---|
  | `taxId` | 50 |
  | `addressLine1`, `addressLine2` | 200 |
  | `postalCode` | 20 |
  | `city`, `region` | 100 |

- R4. `countryCode`: a JSON string or `null`; trimmed; omitted, `null`, empty and whitespace-only mean `null`.
  Otherwise it must be exactly two upper-case ASCII letters (`^[A-Z]{2}$`); it is not checked against a list of
  countries. `"hr"`, `"HRV"`, `"Croatia"` are invalid -> `400` with `errors.countryCode`.
- R5. The address fields are independent: any subset may be filled; none requires another.
- R6. `isActive` is optional on create (default `true`).
- R7. On replace, **every** field of the resource must be present in the body (twelve for a partner, nine for
  a warehouse). Optional text and `countryCode` may be `null`; an omitted one is a validation error under its
  own name, not a silent clearing (001/R5, 002/R11).
- R8. Replace may change every field, including `code` (001/R6: a record keeps its own code without conflict,
  including a change of letter case only). Audit fields behave as 001/R7.
- R9. Booleans (`isCustomer`, `isSupplier`, `isActive`) are JSON `true` or `false`. `null`, a string or a
  number in their place is a value of the wrong type -> `400` with the property's `errors` key (001/E4).
- R10. Unknown body properties are rejected (001/R10, R14) — including `id`, `tenantId`, `address`, `email`,
  `phone`, `createdAt`, `createdBy`. Query strings follow 001/R15; no input may cause `500` (001/R16).
- R11. Order of checks: request validation (`400`, all invalid fields reported together) -> existence of the
  addressed record (`404`) -> code uniqueness (`409 CODE_TAKEN`).
- R12. Delete removes the row. Nothing references a partner or a warehouse yet.
  *Forward notice:* from the specs that add stock and documents (005, 009, 010), a referenced partner or
  warehouse will answer `409 IN_USE` and is retired with `isActive: false` instead (ADR-0008).
- R13. List: `search`, `isActive`, `limit`, `offset`, `total` and ordering (by code, case-insensitive,
  ascending) as 001/R9. Filters combine with AND.

Partners
- R14. `isCustomer` and `isSupplier` are optional on create (default `false` each) and required on replace.
- R15. At least one of `isCustomer`, `isSupplier` must be `true` after create and after replace; otherwise
  `400 VALIDATION_FAILED` with `errors` keys `isCustomer` **and** `isSupplier`. Both may be `true`.
  *Forward notice:* later specs may refuse to remove a role that documents use.
- R16. `taxId` is not unique: any number of partners of one tenant may have the same value.
- R17. Partner `search` matches case-insensitively as a substring of `code`, `name` or `taxId`. Address fields
  are not searched.
- R18. `isCustomer` and `isSupplier` are list filters accepting exactly `true` or `false` (001/R15);
  `isCustomer=true&isSupplier=true` returns partners that are both; `isCustomer=false` returns partners that
  are not customers.

Warehouses
- R19. A warehouse is a place where stock is kept; in this spec it is a named record with an optional address
  and no further behaviour. Warehouse `search` matches `code` or `name`.

MCP
- R20. Rules 003/R12–R17 apply to the ten new tools without exception: a tool contains no rule of its own;
  `*_get` takes exactly one of `id`, `code`; arguments use JSON types; a malformed addressing `id` is
  `NOT_FOUND`; writes are attributed to the key of the MCP request; unknown arguments (including `tenantId`)
  are `VALIDATION_FAILED`.
- R21. For `*_update`, the nullable arguments are **required and nullable**: omitting `taxId` is
  `VALIDATION_FAILED` with `errors.taxId`; `"taxId": null` clears it (as 003/E9).

## 7. Edge cases

- E1. `code` / `name` missing, `null`, empty, whitespace-only, too long, `code` with a forbidden character ->
  `400` with that field's `errors` key (001/E1–E3).
- E2. A partner created with neither role, with both `false`, or with one `false` and the other omitted ->
  `400` with `errors.isCustomer` and `errors.isSupplier`.
- E3. `"isCustomer": null`, `"isCustomer": "true"`, `"isCustomer": 1` -> `400` with `errors.isCustomer` (R9).
- E4. `taxId` of 51 characters -> `400` with `errors.taxId`; exactly 50 -> accepted; `"  "` -> `null`;
  `"HR 123"` (inner space) and `"äö-/.123"` -> accepted unchanged (no format).
- E5. An optional text field containing `\u0000`, `\t` or `\n` -> `400` with its key. A JSON number or object
  in its place -> `400` with its key.
- E6. `countryCode`: `"HR"` and `" HR "` -> `"HR"`; `""` and `"  "` -> `null`; `"hr"`, `"H"`, `"HRV"`, `"H1"`,
  `"12"`, `"ÅB"` -> `400` with `errors.countryCode`. `"XX"` (no such country) -> accepted.
- E7. A request with several invalid fields reports all of them: `{}` on `POST /partners` -> `errors` keys
  `code`, `name`, `isCustomer`, `isSupplier`.
- E8. `PUT` to a random UUID with an invalid body -> `400` (validation first); with a valid body -> `404`.
- E9. `PUT` with a body valid except that its code is taken by another record of the same resource and tenant
  -> `409 CODE_TAKEN`, record unchanged.
- E10. Concurrent creates of the same code in one tenant and resource: exactly one `201`, the rest
  `409 CODE_TAKEN`, none `500`.
- E11. A partner and a warehouse (and an article, and a unit) may share a code.
- E12. `{id}` not a UUID, unknown UUID, unknown or syntactically invalid `{code}` -> `404 NOT_FOUND` (001/E6).
- E13. List: `isCustomer=yes`, `isSupplier=1` -> `400` with that parameter's key; `?country=HR`, `?taxId=1`,
  `?iscustomer=true` -> `400` keyed by the unknown parameter's name; on `/warehouses`, `?isCustomer=true` ->
  `400` with `errors.isCustomer` (not defined for that operation).
- E14. Deleting twice: the second call -> `404`. After a delete the code is free again.

## 8. Tenant isolation

- T1. `Partner` and `Warehouse` implement the tenant-owned marker: global query filter, stamping on insert,
  cross-tenant write check (architecture §3). The model tests of 001/AC-06 and 002/AC-04 cover them unmodified.
- T2. For a key of tenant B, any partner or warehouse of tenant A is non-existent: not listed, not counted,
  not found by any filter or search (including a search for A's exact tax id), `404 NOT_FOUND` on get /
  get-by-code / replace / delete, and no `CODE_TAKEN` across tenants.
- T3. The same holds through tools: `NOT_FOUND` tool errors, empty lists (003/T3).
- T4. The tenant is taken only from the API key, over HTTP and over MCP; `tenantId` in a body, in a query
  string or as a tool argument is an unknown property / parameter / argument and is rejected (001/T1, 003/T1).
- T5. This spec adds no `IgnoreQueryFilters()` and no query outside the tenant-filtered context.

## 9. Security requirements

- S1. All routes require a tenant API key: no credential -> `401 UNAUTHENTICATED`; admin key -> `403 FORBIDDEN`
  (001/S1, S2, S4). The tools are reachable only through `/mcp` with the rules of 003/S1–S2.
- S2. Partner data can be personal data (ADR-0011, consequences). Request and response bodies, tax ids and
  addresses are not written to logs; logs record at most resource, id, key id, tenant id and resulting `code`.
- S3. All database access stays parameterised EF Core LINQ; `search` is escaped as in 001/S7, for all three
  searched partner columns.
- S4. Error bodies and tool errors contain no SQL, constraint names, stack traces or other tenants' data
  (001/S6, 003/S6).
- S5. Free text is stored and returned verbatim (trim aside); the API does not interpret it. It is returned
  only as JSON string values; no response has another content type than `application/json` or
  `application/problem+json`.

## 10. Acceptance criteria

Conventions as in spec 001 §10 and spec 003 §10 ("problem with code X", "MCP client", "tool success",
"tool error X", "equal to HTTP").

**Who tests what** (architecture §9). Criteria without a mark are black-box and belong to the tester: each is
verifiable with HTTP requests to `/api/v1` and MCP requests to `/mcp` alone. Criteria marked *(builder)* are
tested by the builder; *(manual)* ones are checked in review. Every black-box test creates its own tenant(s)
with `POST /api/v1/admin/tenants`; a second key of a tenant comes from `POST /api/v1/api-keys`.

"Partner P" below means a partner created by the test with `{ "code": …, "name": …, "isCustomer": true }`
unless stated otherwise; "the six address properties" are `addressLine1`, `addressLine2`, `postalCode`,
`city`, `region`, `countryCode`.

Structure
- AC-01 *(manual)* `scripts/dotnet.sh build` and `scripts/dotnet.sh test` exit with code 0 from a clean
  checkout; all earlier tests pass; none was weakened, deleted or skipped, and the only earlier test that
  changed is the tool-list test (AC-90). The builder's summary states the commands and the test counts.
- AC-02 *(manual, by inspection)* Exactly one migration was added; earlier migration files are unchanged.
- AC-03 *(builder, model)* The model tests of 001/AC-06 and 002/AC-04 pass, unmodified, with `Partner` and
  `Warehouse` in the model.
- AC-04 *(builder, unit)* The optional-text rule (R3) and the `countryCode` rule (R4) are each implemented once
  in Domain and unit-tested there; the partner role rule (R15) is unit-tested without HTTP or MCP.

Authentication
- AC-10 `GET /partners`, `POST /partners`, `GET /partners/{uuid}`, `GET /warehouses`, `POST /warehouses`,
  `GET /warehouses/{uuid}` with no `Authorization` header and with an unknown key of valid format -> `401`,
  problem with code `UNAUTHENTICATED`, `WWW-Authenticate` starting with `Bearer`.
- AC-11 The same six requests with the admin key -> `403`, problem with code `FORBIDDEN`.

Partners — create and read
- AC-20 `POST /partners` `{ "code": "P-001", "name": "Acme d.o.o.", "isCustomer": true }` -> `201`; body has a
  UUID `id`, `code == "P-001"`, `name == "Acme d.o.o."`, `isCustomer == true`, `isSupplier == false`,
  `taxId == null` and the six address properties `== null` (all seven properties present), `isActive == true`,
  `createdAt == updatedAt`, `createdBy == updatedBy ==` the acting key's id; `Location` ends with
  `/api/v1/partners/{id}`; the body has no `tenantId`.
- AC-21 `POST /partners` with `isSupplier: true`, `isCustomer: true`, `taxId: "HR12345678901"`,
  `addressLine1: "Ilica 1"`, `addressLine2: "2nd floor"`, `postalCode: "10000"`, `city: "Zagreb"`,
  `region: "Grad Zagreb"`, `countryCode: "HR"`, `isActive: false` -> `201` with exactly those values.
  `GET /partners/{id}` -> `200`, JSON-equal to the `201` body. `GET /partners/by-code/<code>` in the original
  and in another letter case -> `200`, same `id`, `code` as stored.
- AC-22 `POST /partners` with `"code": "  P1  "`, `"name": "  Acme  "`, `"taxId": "  X1  "`,
  `"city": "  Split  "`, `"countryCode": " HR "` -> `201` with `P1`, `Acme`, `X1`, `Split`, `HR`.
- AC-23 `POST /partners` with `taxId`, `addressLine1`, `addressLine2`, `postalCode`, `city`, `region` and
  `countryCode` each given as `""`, as `"   "` and as `null` (three requests) -> `201`, each with all seven
  `== null`.
- AC-24 Lengths. Accepted (`201`): code 50, name 200, `taxId` 50, `addressLine1` 200, `addressLine2` 200,
  `postalCode` 20, `city` 100, `region` 100 characters. One character more in any one of them -> `400`
  `VALIDATION_FAILED` with that field's `errors` key.
- AC-25 `POST /partners` with `code` missing, `null`, `""`, `"   "`, `"a b"`, `"a/b"` -> `400`
  `VALIDATION_FAILED` with `errors` key `code`; with `name` missing, `null`, `""`, `"   "` -> `errors` key
  `name`.
- AC-26 Roles. `POST /partners` with neither role, with `isCustomer: false, isSupplier: false`, and with
  `isSupplier: false` alone -> `400` `VALIDATION_FAILED` with `errors` keys `isCustomer` and `isSupplier`; no
  partner is created. With `isSupplier: true` alone -> `201`, `isCustomer == false`, `isSupplier == true`.
- AC-27 `POST /partners` with `"isCustomer": null`, `"isCustomer": "true"`, `"isCustomer": 1` (each with
  `isSupplier: true`) -> `400` `VALIDATION_FAILED` with `errors` key `isCustomer`. With `"isActive": "yes"` (and
  `isCustomer: true`) -> `errors` key `isActive`.
- AC-28 `POST /partners` with `{}` -> `400` with `errors` keys `code`, `name`, `isCustomer` and `isSupplier`
  together.
- AC-29 `countryCode` = `"hr"`, `"H"`, `"HRV"`, `"H1"`, `"12"`, `"Croatia"` -> `400` `VALIDATION_FAILED` with
  `errors` key `countryCode`. `countryCode` = `"XX"` -> `201` with `"XX"`.
- AC-30 Control characters. `POST /partners` with `"a\u0000b"` or `"a\nb"` in `name`, in `taxId`, in
  `addressLine1`, in `city` (one field at a time) -> `400` `VALIDATION_FAILED` with that field's `errors` key.
  `"taxId": 123` -> `400` with `errors` key `taxId`. No response is `500`.
- AC-31 `taxId` has no format and is not unique: `"HR 123"` and `"äö-/.123"` are stored unchanged; two partners
  with the same `taxId` -> both `201`, different ids.
- AC-32 `POST /partners` with an extra property (`"id"`, `"tenantId"`, `"address"`, `"email"`, `"foo"`,
  `"IsCustomer"`), with malformed JSON, or with an empty body -> `400` `VALIDATION_FAILED`, non-empty `errors`;
  `GET /partners` still has `total == 0`.
- AC-33 `POST /partners` of code `P-1` twice, and of `p-1` after `P-1` -> second response `409`, problem with
  code `CODE_TAKEN`. Ten parallel `POST`s of the same new code -> exactly one `201`, nine `409 CODE_TAKEN`.
- AC-34 In one tenant, a unit, an article, a partner and a warehouse can all be created with the code `X-1`
  (`201` each), and each `by-code/X-1` route returns its own record.

Partners — replace
- AC-40 `PUT /partners/{id}` with all twelve fields, every value different from the stored one (new code, new
  name, roles swapped, `taxId` and the six address fields set, `isActive: false`) -> `200` with the new values;
  `id`, `createdAt`, `createdBy` unchanged; `updatedAt` >= previous; a following `GET` returns the same;
  `GET /partners/by-code/<old code>` -> `404`.
- AC-41 `PUT` omitting one of the twelve fields (twelve requests, one field each) -> `400` `VALIDATION_FAILED`
  with that field's `errors` key; the partner is unchanged.
- AC-42 `PUT` with `taxId` and the six address fields `null`, on a partner that has all seven -> `200` with all
  seven `== null`.
- AC-43 `PUT` keeping the partner's own code -> `200`; changing only the letter case of its own code -> `200`
  with the new spelling. `PUT` with the code of another partner of the tenant (any letter case) -> `409`
  `CODE_TAKEN`; the partner is unchanged.
- AC-44 `PUT` with `isCustomer: false` and `isSupplier: false` -> `400` `VALIDATION_FAILED` with `errors` keys
  `isCustomer` and `isSupplier`; the partner is unchanged. `PUT` that turns a customer into a supplier only ->
  `200`.
- AC-45 `PUT` with `countryCode: "hr"`, with a 51-character `taxId`, with `"city": "a\nb"` -> `400` with that
  field's `errors` key; the partner is unchanged.
- AC-46 `PUT` (valid body), `GET` and `DELETE` with a random UUID -> `404`, problem with code `NOT_FOUND`.
  `PUT` to a random UUID with a body whose `name` is `""` -> `400` `VALIDATION_FAILED` (validation precedes
  existence). `GET /partners/not-a-uuid` and `GET /partners/by-code/nope` -> `404` `NOT_FOUND`.
- AC-47 Key K2, created with `POST /api/v1/api-keys` by the tenant's first key K1, replaces a partner created
  by K1 -> `updatedBy == K2`, `createdBy == K1`.

Partners — delete
- AC-50 `DELETE /partners/{id}` -> `204` with empty body; `GET /partners/{id}` -> `404`; a second `DELETE` ->
  `404`; a new `POST` with the same code -> `201`.

Partners — list
- AC-60 In a new tenant, `GET /partners` -> `200` `{ "items": [], "total": 0, "limit": 50, "offset": 0 }`.
- AC-61 With partners `b`, `A`, `c` created in that order, the list returns codes `A`, `b`, `c`, `total == 3`,
  each item JSON-equal to `GET /partners/{id}`.
- AC-62 With 3 partners: `limit=2` -> 2 items, `total == 3`; `limit=2&offset=2` -> the third by code;
  `offset=10` -> 0 items, `total == 3`. `limit=0`, `limit=501`, `limit=abc` -> `400` with `errors` key `limit`;
  `offset=-1` -> `400` with `errors` key `offset`.
- AC-63 `GET /partners?foo=1`, `?country=HR`, `?taxId=1`, `?iscustomer=true` and `GET /partners/{id}?x=1` ->
  `400` `VALIDATION_FAILED` with `errors` key `foo`, `country`, `taxId`, `iscustomer`, `x` respectively.
  `search=a%00b` -> `400` with `errors` key `search`; a `search` of 101 characters -> `400` with `errors` key
  `search`.
- AC-64 `isActive=true` / `isActive=false` / absent return active / inactive / all, `total` reflecting the
  filter; `isActive=maybe` -> `400` with `errors` key `isActive`.
- AC-65 With partners C (customer only), S (supplier only) and B (both): `isCustomer=true` returns C and B;
  `isSupplier=true` returns S and B; `isCustomer=true&isSupplier=true` returns B; `isCustomer=false` returns S;
  `isSupplier=false` returns C; `total` matches each. `isCustomer=yes` -> `400` with `errors` key `isCustomer`;
  `isSupplier=1` -> `400` with `errors` key `isSupplier`.
- AC-66 With partners (`A-1`, `Acme Steel`, taxId `HR111`), (`B-1`, `Baltic Wood`, taxId `DE222`) and
  (`C-2`, `Cedar`, no taxId, city `Steelville`): `search=STEEL` returns only `A-1` (address is not searched);
  `search=-1` returns `A-1` and `B-1`; `search=de22` returns only `B-1`; `search=zzz` returns none; `search=`
  returns all three.
- AC-67 With partners named `100% cotton` and `Plain`: `search=%25` returns only the first; `search=_` returns
  neither.
- AC-68 Filters combine: `isCustomer=true&isActive=true&search=…` returns only partners matching all three.

Warehouses — HTTP
- AC-70 `POST /warehouses` `{ "code": "WH-1", "name": "Main warehouse" }` -> `201`; body has a UUID `id`,
  `code`, `name`, the six address properties `== null` (present), `isActive == true`, `createdAt == updatedAt`,
  `createdBy == updatedBy ==` the acting key's id; `Location` ends with `/api/v1/warehouses/{id}`; the body has
  no `tenantId`, `isCustomer`, `isSupplier` or `taxId` property.
- AC-71 `POST /warehouses` with all six address fields and `isActive: false` -> `201` with those values;
  `GET /warehouses/{id}` JSON-equal to it; `GET /warehouses/by-code/<code>` in either letter case -> same `id`.
- AC-72 Trimming and empties as AC-22 and AC-23 for `code`, `name` and the six address fields.
- AC-73 Lengths as AC-24 for `code`, `name`, `addressLine1`, `addressLine2`, `postalCode`, `city`, `region`.
- AC-74 `POST /warehouses` with `code` or `name` missing, `null`, `""`, `"   "`, or `code` `"a b"` -> `400`
  with that field's `errors` key; `{}` -> `errors` keys `code` and `name`. `countryCode` `"hr"` or `"HRV"` ->
  `errors` key `countryCode`. `"city": "a\u0000b"` or `"a\nb"` -> `errors` key `city`; `"name": "a\nb"` ->
  `errors` key `name`. No response is `500`.
- AC-75 `POST /warehouses` with an extra property (`"id"`, `"tenantId"`, `"isCustomer"`, `"taxId"`, `"foo"`)
  or malformed JSON -> `400` `VALIDATION_FAILED`, non-empty `errors`; the list is still empty.
- AC-76 `POST /warehouses` of `WH-1` twice, and of `wh-1` after `WH-1` -> `409` `CODE_TAKEN`. Ten parallel
  `POST`s of the same new code -> exactly one `201`, nine `409 CODE_TAKEN`.
- AC-77 `PUT /warehouses/{id}` with all nine fields changed -> `200` with the new values; `id`, `createdAt`,
  `createdBy` unchanged; a following `GET` returns the same; the old code's `by-code` route -> `404`.
- AC-78 `PUT` omitting one of the nine fields (nine requests) -> `400` with that field's `errors` key; the
  warehouse is unchanged. `PUT` with the six address fields `null` -> `200`, all six `== null`.
- AC-79 `PUT` keeping its own code, or changing only its letter case -> `200`; with another warehouse's code ->
  `409` `CODE_TAKEN`, unchanged.
- AC-80 `PUT` (valid body), `GET`, `DELETE` with a random UUID -> `404` `NOT_FOUND`; `PUT` to a random UUID
  with `"name": ""` -> `400`; `GET /warehouses/not-a-uuid`, `GET /warehouses/by-code/nope` -> `404`.
- AC-81 `DELETE /warehouses/{id}` -> `204`, empty body; then `GET` -> `404`; second `DELETE` -> `404`; the code
  can be created again.
- AC-82 In a new tenant, `GET /warehouses` -> `200` `{ "items": [], "total": 0, "limit": 50, "offset": 0 }`.
  With warehouses `b`, `A`, `c`: codes in order `A`, `b`, `c`, `total == 3`; `limit=2&offset=2` -> the third;
  `limit=0`, `limit=501` -> `400` with `errors` key `limit`; `offset=-1` -> `errors` key `offset`.
- AC-83 Warehouse list: `isActive` filter as AC-64; `search` matches `code` and `name` case-insensitively and
  not `city`; `search=%25` matches only a literal `%`.
- AC-84 `GET /warehouses?foo=1` and `GET /warehouses?isCustomer=true` -> `400` `VALIDATION_FAILED` with
  `errors` key `foo` / `isCustomer`; `search=a%00b` -> `400` with `errors` key `search`.
- AC-85 Key K2 (created with `POST /api/v1/api-keys`) replaces a warehouse created by K1 -> `updatedBy == K2`,
  `createdBy == K1`.

MCP — tool list
- AC-90 `tools/list` returns exactly 24 tool names: the 14 of 003 §5.3 plus `partner_list`, `partner_get`,
  `partner_create`, `partner_update`, `partner_delete`, `warehouse_list`, `warehouse_get`, `warehouse_create`,
  `warehouse_update`, `warehouse_delete`. The test holds the list literally (it replaces the list of 003/AC-40).
- AC-91 Each of the ten new tools has a non-empty `description`; an `inputSchema` with `type == "object"`,
  `additionalProperties == false`, and `properties` and `required` exactly as in 5.1 (in particular:
  `partner_update` requires all thirteen arguments, `warehouse_update` all ten; `partner_create` requires only
  `code` and `name`); a `description` on every input property; and an `outputSchema`.
- AC-92 The ten new tools have the annotations of 003 §5.3 for their pattern: `*_list` and `*_get`
  `readOnlyHint == true`; `*_create` `readOnlyHint == false`, `destructiveHint == false`,
  `idempotentHint == false`; `*_update` `readOnlyHint == false`, `destructiveHint == true`,
  `idempotentHint == true`; `*_delete` `readOnlyHint == false`, `destructiveHint == true`,
  `idempotentHint == false`; all `openWorldHint == false`.
- AC-93 `tools/list` results for keys of two different tenants are JSON-equal.

MCP — partner tools
- AC-100 `partner_create` `{ "code": "P-001", "name": "Acme", "isCustomer": true }` -> tool success;
  `structuredContent` has a UUID `id`, `isSupplier == false`, `taxId == null`, `createdBy ==` the MCP key's id,
  and is equal to HTTP `GET /partners/{id}`.
- AC-101 `partner_create` with every argument set (roles, `taxId`, six address arguments, `isActive: false`) ->
  tool success with those values, equal to HTTP `GET /partners/{id}`.
- AC-102 `partner_get` `{ "id" }`, `{ "code" }` and `{ "code": <other letter case> }` -> tool success, each
  equal to HTTP `GET /partners/{id}`.
- AC-103 `partner_list` `{}` -> tool success equal to HTTP `GET /partners`; `partner_list`
  `{ "search": "…", "isCustomer": true, "isSupplier": false, "isActive": true, "limit": 1, "offset": 1 }` ->
  equal to HTTP with the same query.
- AC-104 `partner_update` with all thirteen arguments, `taxId` and the six address arguments `null` -> tool
  success, those seven `== null`, `updatedBy ==` the MCP key's id, equal to a following HTTP `GET`.
- AC-105 `partner_delete` `{ "id" }` -> tool success with `structuredContent` `{ "deleted": true }`; HTTP
  `GET /partners/{id}` -> `404`.
- AC-106 `partner_create` `{ "code": "P", "name": "x" }` (no role) -> tool error `VALIDATION_FAILED` with
  `errors` keys `isCustomer` and `isSupplier`; `{ "name": "x", "isCustomer": true }` -> `errors` key `code`;
  `{ …, "countryCode": "hr" }` -> `errors` key `countryCode`; `{ …, "taxId": "a\nb" }` -> `errors` key `taxId`.
  Nothing is created.
- AC-107 `partner_create` with an unknown argument (`"tenantId"`, `"email"`), with `"isCustomer": "true"`, and
  `partner_list` with `{ "isCustomer": "true" }` or `{ "foo": 1 }` -> tool error `VALIDATION_FAILED` with
  non-empty `errors` — not a JSON-RPC error.
- AC-108 `partner_update` without `taxId` (all other arguments valid) -> tool error `VALIDATION_FAILED` with
  `errors` key `taxId`; without `countryCode` -> `errors` key `countryCode`; the partner is unchanged.
- AC-109 `partner_get` with `{}` and with both `id` and `code` -> tool error `VALIDATION_FAILED` with `errors`
  keys `id` and `code`. `partner_get`, `partner_update` (valid arguments) and `partner_delete` with a random
  UUID, `partner_get` `{ "id": "not-a-uuid" }` and `{ "code": "nope" }` -> tool error `NOT_FOUND`.
  `partner_create` of an existing code in another letter case -> tool error `CODE_TAKEN`.

MCP — warehouse tools
- AC-110 `warehouse_create` `{ "code": "WH-1", "name": "Main" }` -> tool success, six address properties
  `== null`, `createdBy ==` the MCP key's id, equal to HTTP `GET /warehouses/{id}`. With all six address
  arguments -> tool success with those values, equal to HTTP.
- AC-111 `warehouse_get` by `id` and by `code`; `warehouse_list` `{}` and
  `{ "search": "…", "isActive": true, "limit": 1, "offset": 1 }` -> tool success, each equal to the
  corresponding HTTP request.
- AC-112 `warehouse_update` with all ten arguments (address arguments `null`) -> tool success, equal to a
  following HTTP `GET`, `updatedBy ==` the MCP key's id. `warehouse_delete` -> `{ "deleted": true }`; HTTP
  `GET` -> `404`.
- AC-113 `warehouse_create` without `code` -> tool error `VALIDATION_FAILED` with `errors` key `code`; with
  `"countryCode": "HRV"` -> `errors` key `countryCode`; with an unknown argument (`"isCustomer"`, `"tenantId"`)
  -> tool error `VALIDATION_FAILED`. `warehouse_update` without `city` -> `errors` key `city`.
- AC-114 `warehouse_get` with `{}` or both `id` and `code` -> tool error `VALIDATION_FAILED` with `errors` keys
  `id` and `code`; `warehouse_get` / `warehouse_update` / `warehouse_delete` with a random UUID -> tool error
  `NOT_FOUND`; `warehouse_create` of an existing code -> tool error `CODE_TAKEN`.

MCP — parity and attribution
- AC-120 For each of: `partner_create` without a role; `partner_create` with `countryCode: "hr"` and a
  51-character `taxId` together; `partner_create` of a taken code; `warehouse_create` with `{}` — the same
  input sent to the HTTP endpoint yields a problem document with the same `code` and the same set of `errors`
  keys as the tool error.
- AC-121 A partner created over HTTP by key H and updated through `partner_update` by key M has
  `createdBy == H` and `updatedBy == M`; the same for a warehouse and `warehouse_update`.

Tenant isolation (tenants A and B, each with its own key and its own MCP client)
- AC-130 A creates partner `X1` (taxId `T-777`, customer) and warehouse `W1`. B's `GET /partners` and
  `GET /warehouses` -> `items` empty, `total == 0`; B's `GET /partners?search=T-777`, `?search=X1`,
  `?isCustomer=true` and `GET /warehouses?search=W1` return nothing.
- AC-131 B: `GET /partners/{A's partner id}`, `GET /partners/by-code/X1`, `GET /warehouses/{A's warehouse id}`,
  `GET /warehouses/by-code/W1` -> `404`, problem with code `NOT_FOUND`.
- AC-132 B: `PUT` with a valid body and `DELETE` on A's partner id and on A's warehouse id -> `404`
  `NOT_FOUND`; A then reads both records unchanged.
- AC-133 B: `POST /partners` with code `X1` and `POST /warehouses` with code `W1` -> `201` (no `CODE_TAKEN`
  across tenants); ids differ from A's; each tenant's `by-code` route returns its own record.
- AC-134 B: `POST /partners` with an extra property `"tenantId": <A's tenant id>` -> `400`
  `VALIDATION_FAILED`; `GET /partners?tenantId=<A's tenant id>` -> `400` with `errors` key `tenantId`;
  nothing is created in either tenant and A's data is not returned.
- AC-135 Through tools, B: `partner_list` and `warehouse_list` -> empty, `total == 0`, also with
  `{ "search": "T-777" }`; `partner_get` and `warehouse_get` with A's ids and with A's codes;
  `partner_update`, `partner_delete`, `warehouse_update`, `warehouse_delete` with A's ids -> tool error
  `NOT_FOUND`. A reads both records unchanged.
- AC-136 Through tools, B: `partner_create` `X1` and `warehouse_create` `W1` -> tool success;
  `partner_create` with an argument `"tenantId": <A's tenant id>` -> tool error `VALIDATION_FAILED`, nothing
  created.
- AC-137 A partner created by B through a tool is listed by B over HTTP and is not listed by A over HTTP or
  through `partner_list`.
- AC-138 Two MCP clients, one per tenant, issuing 20 interleaved `partner_create` / `partner_list` calls in
  parallel: every `partner_list` result contains only codes created by its own tenant.

Errors
- AC-140 Every HTTP error response asserted above has `Content-Type: application/problem+json` and a body with
  `code`, `status`, `title` and `detail`. No asserted response has status `500` and no tool result has code
  `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- All criteria except AC-01…AC-04 are yours. They need only HTTP and MCP; if you find one that cannot be
  tested without an internal type or a look into the database, that is a defect of this spec — write it in
  `docs/questions/004-q.md`.
- Partner and warehouse criteria are deliberately parallel; a shared helper for "a master with code, name and
  address" is welcome, but each criterion still needs a test that fails for its own resource.
- The parity helper of spec 003 (tool `structuredContent` versus HTTP body) is the tool for AC-100…AC-121.
- Update the literal tool list of 003/AC-40 to the 24 names (AC-90); do not keep a second, 14-name test.

Builder
- Implement the optional-text and `countryCode` rules once in Domain (a small value object or rule class used
  by both entities), and the address field set once (an owned type or a shared DTO fragment) — but the wire
  format stays flat: six top-level properties, six top-level tool arguments, `errors` keyed by the plain field
  name (`city`, not `address.city`).
- `PUT` and `*_update` must distinguish an omitted nullable field from an explicit `null` (R7, R21), as spec
  002 did for `description`. Reuse that mechanism.
- Input and output schemas of the tools derive from the same DTOs as the HTTP endpoints (003 §11).
- Do not build anything for references to partners or warehouses yet; ADR-0008's mechanism from spec 002 will
  be reused by the specs that add them. The unique key `(TenantId, Id)` on both tables is the only preparation.
- The partner search over three columns must stay one query with escaped patterns (S3).
- Anything unclear or contradictory, including a tester's test you believe is wrong:
  `docs/questions/004-q.md`, then continue with the rest.
