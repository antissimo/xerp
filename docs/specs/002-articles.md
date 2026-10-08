# Spec 002 — Articles (item master)

Status: ready for implementation once spec 001 is merged to `main`. Branch: `feat/002-articles`.
Read first: `docs/architecture.md`, `docs/specs/001-foundation-uom.md`, ADR-0002, ADR-0004, **ADR-0008**.
Why this is the second spec: `docs/roadmap.md` section 2 and backlog item 2.

Everything spec 001 established applies unchanged unless this spec says otherwise: authentication, tenant
context, error model, list envelope, code and name rules, audit fields, test infrastructure. Rule, edge-case and
criterion numbers below are local to this spec; "001/R9" means rule R9 of spec 001.

## 1. Goal

Add the first real master record, the **article** (item: a thing the company stocks, buys or sells, or a service
it sells), and with it the first reference between two tenant-owned records: every article has a **base unit of
measure**. After this spec:

- a tenant API key can list, read, create, replace and delete its tenant's articles;
- an article always points at an existing unit of measure of the same tenant;
- a unit of measure that is used by an article can no longer be deleted (`409 IN_USE`);
- the reference rules of ADR-0008 (ids in, summary out, `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`,
  tenant-inclusive foreign key) are implemented once, in a form later specs reuse.

## 2. Scope

In scope
1. `Article` entity, table, migration; Application operations; HTTP endpoints under `/api/v1/articles`.
2. Reference handling per ADR-0008 for `Article.baseUnit`.
3. Change to an existing operation: `DELETE /api/v1/units-of-measure/{id}` returns `409 IN_USE` when the unit is
   referenced by an article. This supersedes 001/R8 ("delete removes the row") for referenced units only.

Out of scope
- MCP server (the MCP signatures in section 5 are a contract for spec 003, **not** to be implemented now).
- Stock, ledger entries, quantities on hand (spec 008). Until then nothing references an article, so an article
  can always be deleted.
- Alternative units and conversion factors per article (spec 009); prices and price lists; tax classes
  (spec 015); barcodes; article groups/categories; images/attachments; lots and serial numbers.
- Reference by code in request bodies (ADR-0008, decision 1).

## 3. Data

`Article` (tenant-owned): `Id` uuid v7, `TenantId`, `Code`, `Name`, `Description` (nullable, max 2000),
`Type` (`stock` | `service`), `BaseUnitId`, `IsActive`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`.

Indexes and keys
- Unique `(Article.TenantId, Article.Code)`, case-insensitive on `Code`, enforced by the database (as 001 §3).
- Foreign key `(Article.TenantId, Article.BaseUnitId)` -> `(UnitOfMeasure.TenantId, UnitOfMeasure.Id)`,
  `ON DELETE RESTRICT` (or `NO ACTION`); no cascade. `UnitOfMeasure` gets the unique key on `(TenantId, Id)`
  that this requires.
- An index that makes "articles by base unit" efficient (the foreign-key columns).

Migration: one **new** migration is added on top of spec 001's initial migration, which is not edited.

## 4. Operations — HTTP

All routes are under `/api/v1` and require `Authorization: Bearer <tenant API key>`.

Representation (`Article`):
```json
{ "id": "uuid", "code": "ART-001", "name": "Steel bolt M8", "description": null,
  "type": "stock",
  "baseUnit": { "id": "uuid", "code": "pcs", "name": "Piece" },
  "isActive": true,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```
`description` is always present (`null` when empty). `baseUnit` is the unit's current `id`, `code` and `name`.
The representation contains no `baseUnitId` and no `tenantId`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /articles?search=&type=&baseUnitId=&isActive=&limit=&offset=` | `200` `{ "items": [Article], "total": int, "limit": int, "offset": int }` | 400, 401, 403 |
| Get | `GET /articles/{id}` | `200` Article | 401, 403, 404 |
| Get by code | `GET /articles/by-code/{code}` | `200` Article | 401, 403, 404 |
| Create | `POST /articles` body `{ "code", "name", "type", "baseUnitId", "description"?, "isActive"? }` | `201` Article, `Location: /api/v1/articles/{id}` | 400, 401, 403, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE`, 409 `CODE_TAKEN` |
| Replace | `PUT /articles/{id}` body `{ "code", "name", "description", "type", "baseUnitId", "isActive" }` | `200` Article | 400, 401, 403, 404, 409 `REFERENCE_NOT_FOUND`, 409 `REFERENCE_INACTIVE`, 409 `CODE_TAKEN` |
| Delete | `DELETE /articles/{id}` | `204`, empty body | 401, 403, 404 |

Changed operation

| Operation | Request | Success | Errors |
|---|---|---|---|
| Delete unit | `DELETE /units-of-measure/{id}` | `204`, empty body | 401, 403, 404, **409 `IN_USE`** |

New error codes (added to the registry in `docs/architecture.md` section 6):

| HTTP | `code` | When | Body |
|---|---|---|---|
| 409 | `REFERENCE_NOT_FOUND` | `baseUnitId` is a well-formed UUID but no such unit exists in the caller's tenant. | `errors` has key `baseUnitId` |
| 409 | `REFERENCE_INACTIVE` | The unit exists but is inactive and is being newly assigned (R9). | `errors` has key `baseUnitId` |

## 5. Operations — MCP (contract only; implemented in spec 003)

Same field names, limits and error codes as HTTP; errors are tool errors with `{ "code", "detail", "errors"? }`.

| Tool | Input | Output |
|---|---|---|
| `article_list` | `{ search?: string, type?: "stock"\|"service", baseUnitId?: uuid, isActive?: boolean, limit?: int, offset?: int }` | `{ items, total, limit, offset }` |
| `article_get` | `{ id?: uuid, code?: string }` — exactly one of the two | Article |
| `article_create` | `{ code: string, name: string, type: "stock"\|"service", baseUnitId: uuid, description?: string\|null, isActive?: boolean }` | Article |
| `article_update` | `{ id: uuid, code: string, name: string, description: string\|null, type: "stock"\|"service", baseUnitId: uuid, isActive: boolean }` | Article |
| `article_delete` | `{ id: uuid }` | `{ deleted: true }` |

`uom_delete` (001 §5) can now fail with `IN_USE`.

As in spec 001: every operation in section 4 exists as an Application-layer operation with plain inputs and no
ASP.NET Core types. The reference check, the `IN_USE` check and the order of checks (R13) are Application rules,
not endpoint code.

## 6. Business rules

Code and name
- R1. `code` follows 001/R1–R3 (trimmed; `^[\p{L}\p{N}._-]{1,50}$`; stored as entered; unique and looked up
  case-insensitively **within the tenant's articles**). Article codes and unit-of-measure codes are separate
  namespaces: an article may have the code `kg`.
- R2. `name` follows 001/R4 (trimmed; 1–200 characters).

Description
- R3. Optional free text. Trimmed; an empty or whitespace-only value is stored and returned as `null`; maximum
  2000 characters after trimming. Line breaks inside the text are preserved.

Type
- R4. `type` is required and is exactly `"stock"` or `"service"` (lower-case; `"Stock"` is invalid).
  `stock` = a physical item whose quantity will be tracked in the stock ledger (spec 008);
  `service` = never stocked. In this spec the value is stored and filterable and has no other effect.

Base unit
- R5. `baseUnitId` is required on create and on replace. It must be a UUID string.
- R6. The unit must exist in the caller's tenant; otherwise `409 REFERENCE_NOT_FOUND`.
- R7. Both article types require a base unit (a service is sold in hours, pieces, …).
- R8. The article representation shows the unit's **current** code and name: replacing the unit's code or name
  is immediately visible on its articles.
- R9. An inactive unit cannot be newly assigned: on create, and on replace when `baseUnitId` differs from the
  article's current base unit, an inactive unit gives `409 REFERENCE_INACTIVE`. A replace that keeps the current
  base unit succeeds even if that unit has since been deactivated.

Create and replace
- R10. `isActive` is optional on create (default `true`). `description` is optional on create (default `null`).
- R11. On replace all six fields are required to be **present**: `code`, `name`, `description`, `type`,
  `baseUnitId`, `isActive`. `description` may be `null`; an omitted `description` is a validation error, not a
  silent clearing (same reasoning as 001/R5).
- R12. Replace may change every field, including `code` (001/R6 applies: own code, or a change of letter case
  only, never conflicts), `type` and `baseUnitId`. Audit fields behave as 001/R7.
  *Forward notice:* once stock ledger entries exist for an article (spec 008), `type` and `baseUnitId` will
  become unchangeable for that article. Clients must not rely on changing them after first use.
- R13. Order of checks (ADR-0008, decision 7): request validation -> existence of the addressed article (`404`)
  -> base unit (`REFERENCE_NOT_FOUND`, then `REFERENCE_INACTIVE`) -> code uniqueness (`CODE_TAKEN`).
  A request that fails validation reports all invalid fields at once and performs no database lookup of the unit.
- R14. Unknown body properties are rejected (001/R10) — including `id`, `tenantId`, `baseUnit`, `baseUnitCode`,
  `createdAt`, `createdBy`.

Delete
- R15. `DELETE /articles/{id}` removes the row. Nothing references an article yet; spec 008 introduces `IN_USE`
  for articles.
- R16. `DELETE /units-of-measure/{id}` fails with `409 IN_USE` while at least one article of the tenant —
  active or inactive — has it as base unit; the unit is left unchanged. Once no article references it, delete
  works as in spec 001.
- R17. Deactivating a unit (`isActive: false`) and changing its code or name are always allowed, referenced or not.

List
- R18. As 001/R9 for `search` (trimmed, max 100, case-insensitive substring of `code` or `name`, wildcards
  literal; `description` is not searched), `isActive`, `limit`, `offset`, `total` and ordering (by code,
  case-insensitive, ascending).
- R19. `type` filters by exact value when present; `baseUnitId` filters by base unit when present. All filters
  combine with AND. A `baseUnitId` that is a well-formed UUID but matches no unit of the tenant is not an error:
  the result is empty.

## 7. Edge cases

- E1. `code`/`name` missing, `null`, empty, whitespace-only, too long, or `code` with a forbidden character ->
  `400`, `errors` has that field's key (as 001/E1–E3).
- E2. `type` missing, `null`, `""`, `"Stock"`, `"goods"` -> `400` with `errors.type`.
- E3. `baseUnitId` missing, `null`, `""`, `"abc"`, `"kg"` (a unit's code instead of its id) -> `400` with
  `errors.baseUnitId`. A JSON number or object in its place -> `400` with non-empty `errors` (key not specified,
  001/E4).
- E4. `description` of 2001 characters -> `400` with `errors.description`; exactly 2000 -> accepted;
  `""` or `"   "` -> accepted, stored as `null`.
- E5. `baseUnitId` = a random UUID, or the id of a unit that was deleted -> `409 REFERENCE_NOT_FOUND`.
- E6. A request with an invalid `name` **and** an unknown `baseUnitId` -> `400` (validation first).
  A request with an unknown `baseUnitId` **and** a code already taken -> `409 REFERENCE_NOT_FOUND`.
- E7. `PUT` on a non-existent article id with an otherwise valid body whose `baseUnitId` is unknown -> `404`.
- E8. Concurrent creates of the same article code in one tenant: exactly one `201`, the rest `409 CODE_TAKEN`,
  none `500`.
- E9. An article is created with unit U while U is being deleted: either the article is created and the delete
  gets `409 IN_USE`, or the delete succeeds and the create gets `409 REFERENCE_NOT_FOUND`. Never `500`, and
  never an article whose base unit does not exist.
- E10. List filters: `type=goods` -> `400` with `errors.type`; `baseUnitId=abc` -> `400` with
  `errors.baseUnitId`; `baseUnitId=<random UUID>` -> `200`, empty.
- E11. `{id}` not a UUID, unknown UUID, unknown or syntactically invalid `{code}` -> `404 NOT_FOUND` (001/E6).
- E12. Deleting an article twice: second call -> `404`. After the only referencing article is deleted, or is
  moved to another base unit, the unit can be deleted.

## 8. Tenant isolation

- T1. `Article` implements the tenant-owned marker: global query filter, stamping on insert, cross-tenant write
  check (architecture §3). The model test of 001/AC-06 covers it without modification.
- T2. For a key of tenant B, any article of tenant A is non-existent: not listed, not counted, `404 NOT_FOUND`
  on get / get-by-code / replace / delete, and no `CODE_TAKEN` across tenants.
- T3. A unit of tenant A is non-existent as a reference for tenant B: `baseUnitId` = A's unit id gives exactly
  the response a random UUID gives (`409 REFERENCE_NOT_FOUND`); as a list filter it gives an empty result.
- T4. Articles of tenant A never make a unit of tenant B `IN_USE`.
- T5. The database itself refuses a cross-tenant reference: the foreign key includes `TenantId`
  (architecture §3, rule 4), so even code that bypassed the Application checks could not store an article of
  one tenant pointing at a unit of another.
- T6. The reference lookup and the `IN_USE` check go through the tenant-filtered context; no
  `IgnoreQueryFilters()` is added by this spec.

## 9. Security requirements

- S1. All article routes require a tenant API key: no credential -> `401 UNAUTHENTICATED`; admin key ->
  `403 FORBIDDEN` (001/S1, S2, S4).
- S2. Reference errors reveal nothing about other tenants: status, `code`, `errors` keys and `detail` text for
  another tenant's unit id are identical to those for a non-existent id (the echoed id aside).
- S3. `IN_USE` may state how many articles reference the unit; it lists no data of the referrers.
- S4. All database access stays parameterised EF Core LINQ; `search` is escaped as in 001/S7. Tests that touch
  the database directly are test code only.
- S5. Error bodies contain no SQL, constraint names or stack traces; an unexpected foreign-key or unique
  violation is translated (ADR-0008, decision 6) or becomes `500 INTERNAL_ERROR` without details (001/S6).

## 10. Acceptance criteria

Conventions as in spec 001 §10: each criterion maps to at least one named test unless marked *(manual)*;
"problem with code X" means `Content-Type: application/problem+json`, body `code == "X"`, `status` equal to the
HTTP status; tests assert status, `code` and stated `errors` keys, never message text. Each test creates its own
tenant(s) through `POST /api/v1/admin/tenants` and its own units through `POST /api/v1/units-of-measure`.
"Unit U" below means an active unit created by the test in the same tenant unless stated otherwise.

Structure
- AC-01 *(manual)* `scripts/dotnet.sh build` and `scripts/dotnet.sh test` exit with code 0 from a clean checkout.
  All tests of spec 001 still pass and none was weakened, deleted or skipped. The builder's summary states the
  commands run and the test counts.
- AC-02 *(manual, by inspection)* Exactly one migration was added; spec 001's migration files are unchanged.
- AC-03 (integration, model) The model test of 001/AC-06 passes with `Article` in the model, unmodified.
- AC-04 (integration, model) In the EF Core model, every foreign key whose dependent and principal entity types
  are both tenant-owned includes `TenantId` among its dependent properties and its principal key properties, and
  its delete behaviour is not cascade and not set-null. The test enumerates the model (it must fail for a future
  entity that violates this) and asserts that it found at least the `Article` -> `UnitOfMeasure` key.
- AC-05 (integration, below HTTP) Inserting, with raw SQL, an article row with tenant B's `TenantId` and the id
  of a unit of tenant A as base unit fails with a foreign-key violation and stores no row.
- AC-06 (integration, below HTTP) Deleting, with raw SQL, a unit row that an article references fails with a
  foreign-key violation; both rows still exist.

Authentication
- AC-10 `GET /api/v1/articles`, `GET /api/v1/articles/{uuid}`, `POST /api/v1/articles` with no `Authorization`
  header and with an unknown key of valid format -> `401`, problem with code `UNAUTHENTICATED`,
  `WWW-Authenticate` starting with `Bearer`.
- AC-11 `GET /api/v1/articles` and `POST /api/v1/articles` with the admin key -> `403`, problem with code
  `FORBIDDEN`.

Create and read
- AC-20 `POST` `{ "code": "ART-001", "name": "Steel bolt M8", "type": "stock", "baseUnitId": U }` -> `201`;
  body has a UUID `id`, `code == "ART-001"`, `name == "Steel bolt M8"`, `description == null` (property
  present), `type == "stock"`, `baseUnit.id == U`, `baseUnit.code` and `baseUnit.name` equal to U's,
  `isActive == true`, `createdAt == updatedAt`, `createdBy == updatedBy ==` the acting key's id; `Location` ends
  with `/api/v1/articles/{id}`; the body contains neither `tenantId` nor `baseUnitId`.
- AC-21 `GET /{id}` -> `200` with the same representation. `GET /by-code/ART-001` and `GET /by-code/art-001` ->
  `200`, same `id`, `code == "ART-001"`.
- AC-22 `POST` with `"code": "  A1  "`, `"name": "  Bolt  "`, `"description": "  line1\nline2  "` -> `201` with
  `code == "A1"`, `name == "Bolt"`, `description == "line1\nline2"`.
- AC-23 `POST` with `"description": ""`, with `"description": "   "` and with `"description": null` -> `201`,
  each with `description == null`.
- AC-24 `POST` with `"type": "service"` and `"isActive": false` -> `201` with `type == "service"`,
  `isActive == false`, and a `baseUnit`.
- AC-25 `POST` with a 50-character code, a 200-character name and a 2000-character description -> `201`.
  A 51-character code -> `400` with `errors` key `code`; a 201-character name -> `400` with `errors` key `name`;
  a 2001-character description -> `400` with `errors` key `description`.
- AC-26 `POST` with `code` missing, `null`, `""`, `"   "`, `"a b"`, `"a/b"`, `"a%"` -> `400`
  `VALIDATION_FAILED` with `errors` key `code`. With `name` missing, `null`, `""`, `"   "` -> `400` with
  `errors` key `name`.
- AC-27 `POST` with `type` missing, `null`, `""`, `"Stock"`, `"goods"` -> `400` `VALIDATION_FAILED` with
  `errors` key `type`.
- AC-28 `POST` with `baseUnitId` missing, `null`, `""`, `"abc"`, and U's **code** instead of its id -> `400`
  `VALIDATION_FAILED` with `errors` key `baseUnitId`. With `"baseUnitId": 123` -> `400` `VALIDATION_FAILED` with
  non-empty `errors`.
- AC-29 `POST` whose body is `{}` -> `400` with `errors` keys `code`, `name`, `type` and `baseUnitId` together.
- AC-30 `POST` with an extra property (`"id"`, `"tenantId"`, `"baseUnit"`, `"baseUnitCode"` or `"foo"`), with
  malformed JSON, or with an empty body -> `400` `VALIDATION_FAILED`, non-empty `errors`; the tenant's article
  list is still empty.
- AC-31 `POST` of code `ART-1` twice, and of `art-1` after `ART-1` -> second response `409`, problem with code
  `CODE_TAKEN`.
- AC-32 Ten parallel `POST`s of the same new article code in one tenant -> exactly one `201`, nine
  `409 CODE_TAKEN`, no other status.
- AC-33 In a tenant with a unit whose code is `kg`, `POST` of an article with code `kg` -> `201`
  (separate code namespaces); `GET /units-of-measure/by-code/kg` and `GET /articles/by-code/kg` return the unit
  and the article respectively.

References (base unit)
- AC-40 `POST` with `baseUnitId` = a random UUID -> `409`, problem with code `REFERENCE_NOT_FOUND`, `errors`
  has key `baseUnitId`; no article is created.
- AC-41 `POST` with `baseUnitId` = the id of a unit that was created and then deleted -> `409`
  `REFERENCE_NOT_FOUND`.
- AC-42 `POST` with `baseUnitId` = an inactive unit of the same tenant -> `409`, problem with code
  `REFERENCE_INACTIVE`, `errors` has key `baseUnitId`; no article is created.
- AC-43 `POST` with a whitespace-only `name` and a random-UUID `baseUnitId` -> `400` `VALIDATION_FAILED` with
  `errors` key `name` (validation precedes the reference check).
- AC-44 `POST` with a code that is already taken and a random-UUID `baseUnitId` -> `409` `REFERENCE_NOT_FOUND`
  (reference precedes uniqueness).
- AC-45 After `PUT` on unit U changes its code and name, `GET /articles/{id}` of an article with base unit U
  shows the new `baseUnit.code` and `baseUnit.name` and the same `baseUnit.id`; the article's `updatedAt` and
  `updatedBy` are unchanged.

Replace
- AC-50 `PUT /{id}` `{ "code": "ART-002", "name": "Bolt M10", "description": "zinc", "type": "service",
  "baseUnitId": U2, "isActive": false }` (U2 another active unit) -> `200` with the new values and
  `baseUnit.id == U2`; `id`, `createdAt`, `createdBy` unchanged; `updatedAt` >= previous; a following `GET /{id}`
  returns the same; `GET /by-code/ART-001` -> `404`.
- AC-51 `PUT` omitting `code`, `name`, `description`, `type`, `baseUnitId` or `isActive` (one at a time) ->
  `400` `VALIDATION_FAILED` with that field's `errors` key; the article is unchanged.
- AC-52 `PUT` with `"description": null` on an article that has a description -> `200` with
  `description == null`.
- AC-53 `PUT` keeping the article's own code -> `200`; changing only the letter case of its own code -> `200`
  with the new spelling.
- AC-54 `PUT` setting the code to that of another article of the same tenant (any letter case) -> `409`
  `CODE_TAKEN`; the article is unchanged.
- AC-55 `PUT` with `baseUnitId` = a random UUID -> `409` `REFERENCE_NOT_FOUND` with `errors` key `baseUnitId`;
  `PUT` with `baseUnitId` = a different, inactive unit -> `409` `REFERENCE_INACTIVE` with `errors` key
  `baseUnitId`; in both cases the article is unchanged.
- AC-56 Article with base unit U; U is then deactivated. `PUT` on the article keeping `baseUnitId == U` and
  changing `name` -> `200`. `POST` of a new article with `baseUnitId == U` -> `409` `REFERENCE_INACTIVE`.
- AC-57 `PUT`, `GET` and `DELETE` with a random UUID -> `404`, problem with code `NOT_FOUND` (the `PUT` body is
  valid except that its `baseUnitId` is a random UUID). `GET /articles/not-a-uuid` -> `404` `NOT_FOUND`.
  `GET /articles/by-code/nope` -> `404` `NOT_FOUND`.
- AC-58 When a second key of the same tenant (inserted by the test directly in the database) replaces an
  article, `updatedBy` is the second key's id and `createdBy` is still the first key's id.

Delete
- AC-60 `DELETE /articles/{id}` -> `204` with empty body; then `GET /{id}` -> `404`; second `DELETE` -> `404`;
  a new `POST` with the same code -> `201`.

List
- AC-70 In a new tenant, `GET /articles` -> `200` `{ "items": [], "total": 0, "limit": 50, "offset": 0 }`.
- AC-71 With articles `b`, `A`, `c` created in that order, list returns codes `A`, `b`, `c` and `total == 3`;
  every item has the full representation including `baseUnit`.
- AC-72 With 3 articles: `limit=2` -> 2 items, `total == 3`; `limit=2&offset=2` -> the third by code;
  `offset=10` -> 0 items, `total == 3`.
- AC-73 `limit=0`, `limit=501`, `limit=abc` -> `400` with `errors` key `limit`; `offset=-1` -> `400` with
  `errors` key `offset`.
- AC-74 `isActive=true` / `isActive=false` / absent return active / inactive / all; `total` reflects the filter;
  `isActive=maybe` -> `400` with `errors` key `isActive`.
- AC-75 With two `stock` articles and one `service` article: `type=stock` returns the two, `type=service` the
  one, `total` matching; `type=goods` and `type=Stock` -> `400` with `errors` key `type`.
- AC-76 With two articles on unit U1 and one on unit U2: `baseUnitId=U1` returns the two, `baseUnitId=U2` the
  one; `baseUnitId=<random UUID>` -> `200`, 0 items, `total == 0`; `baseUnitId=abc` -> `400` with `errors` key
  `baseUnitId`.
- AC-77 Filters combine: `type=stock&isActive=true&baseUnitId=U1&search=…` returns only articles matching all
  four.
- AC-78 With articles (`B-8`, `Steel bolt`), (`N-8`, `Steel nut`), (`W-1`, `Washer`, description `bolt washer`):
  `search=BOLT` returns only `B-8` (description is not searched); `search=steel` returns `B-8` and `N-8`;
  `search=-8` returns `B-8` and `N-8`; `search=zzz` returns none; `search=` returns all three.
- AC-79 With articles (`c1`, `100% cotton`) and (`p1`, `Plain`): `search=%25` returns only `c1`; `search=_`
  returns neither. A `search` of 101 characters -> `400` with `errors` key `search`.

Units of measure — `IN_USE`
- AC-80 With an article on unit U: `DELETE /units-of-measure/{U}` -> `409`, problem with code `IN_USE`;
  `GET /units-of-measure/{U}` -> `200`, unchanged.
- AC-81 The same when the only referencing article is inactive.
- AC-82 After the referencing article is deleted, `DELETE /units-of-measure/{U}` -> `204`. Likewise after the
  referencing article is replaced with another `baseUnitId`.
- AC-83 A unit referenced by no article is deleted with `204` (spec 001 behaviour unchanged).
- AC-84 A referenced unit can be replaced with `isActive: false` and with a new code and name -> `200`.
- AC-85 Race: in each of 20 rounds the test creates a fresh unit U and then sends, in parallel, `POST /articles`
  referencing U and `DELETE /units-of-measure/{U}`. In every round the pair of results is either
  (`201`, `409 IN_USE`) or (`409 REFERENCE_NOT_FOUND`, `204`) — no other status — and afterwards
  `GET /units-of-measure/{U}` is `200` exactly when the article was created.

Tenant isolation (tenants A and B, each with its own key and its own units)
- AC-90 A creates article `X1`. B's list -> `items` empty, `total == 0`; B's `search`, `type` and `baseUnitId`
  filters (including `baseUnitId` = A's unit id) return nothing.
- AC-91 B: `GET /articles/{A's article id}` -> `404 NOT_FOUND`; `GET /articles/by-code/X1` -> `404 NOT_FOUND`.
- AC-92 B: `PUT /articles/{A's article id}` with a body valid in B -> `404 NOT_FOUND`; A reads its article
  unchanged.
- AC-93 B: `DELETE /articles/{A's article id}` -> `404 NOT_FOUND`; A can still read its article.
- AC-94 B: `POST` of an article with code `X1` on B's own unit -> `201`; different ids; each tenant's
  `by-code/X1` returns its own article with its own `baseUnit`.
- AC-95 B: `POST` of an article with `baseUnitId` = A's unit id -> `409` `REFERENCE_NOT_FOUND` with `errors`
  key `baseUnitId`; apart from the echoed id, the response body is identical to the one B gets for a random
  UUID (same `code`, `status`, `title`, `errors` keys). The same for `PUT` on B's own article.
- AC-96 A and B each have a unit with code `kg`; A has an article on its `kg`. B's
  `DELETE /units-of-measure/{B's kg}` -> `204`; A's `DELETE /units-of-measure/{A's kg}` -> `409 IN_USE`.
- AC-97 (integration, below HTTP) Saving an `Article` whose `TenantId` is tenant A through a DbContext whose
  current tenant is B fails and writes no row (as 001/AC-67).

Errors
- AC-98 Every error response asserted above has `Content-Type: application/problem+json` and a body with
  `code`, `status`, `title` and `detail`; no asserted response has status `500`.

## 11. Notes for the builder

- Start from `main` after spec 001 is merged. Do not edit spec 001's migration; add one.
- ADR-0008 decision 6: pre-check the unit in Application (gives `REFERENCE_NOT_FOUND` / `REFERENCE_INACTIVE` /
  `IN_USE` on the ordinary path), but AC-85 only passes if the foreign-key violation raised by the database is
  translated in Infrastructure — Application must not reference Npgsql. Reuse the mechanism spec 001 built for
  `CODE_TAKEN`, extended to tell a violated reference on write from a blocked delete.
- Build the reference check and the `{ id, code, name }` summary so that specs 004 and 008 can reuse them for
  partners, warehouses and document lines; do not build a generic framework beyond what two call sites need.
- `PUT` must distinguish an omitted `description` from `"description": null` (R11, AC-51 vs AC-52).
- List queries must not issue one query per article for `baseUnit` (join or include).
- Anything unclear or contradictory: `docs/questions/002-q.md`, then continue with the rest.
