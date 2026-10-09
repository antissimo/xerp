# Spec 005 — Stock ledger, receipts and issues

Status: ready for the tester and the builder once specs 001–004 are merged to `main`.
Branches: `tests/005-stock-ledger-receipts-issues` (tester), `feat/005-stock-ledger-receipts-issues` (builder).
Read first: `docs/architecture.md`, specs 002 and 004 (articles, warehouses), ADR-0007, ADR-0008,
**ADR-0012**. Why this is the fifth spec: `docs/roadmap.md` section 3 (business functionality first).
Second half of the stock core — transfers and reversal — is spec 006.
Spec 006 adds properties to the representations of §4.1 and §4.3 (`toWarehouse`, `reversalOf`, `reversedBy`,
`document.isReversal`) and makes `type` `"transfer"` valid; tests of this spec that pin those are changed there.
Spec 007 adds `unitId` to a line and `factor`, `baseUnit`, `baseQuantity` to its representation (007/AC-01). Spec 008 adds `type` `"count"` and `bookQuantity`, `differenceQuantity` on a line.

**Inherited, not re-specified** (roadmap §4): authentication and credential kinds, strict bodies and query
strings, the problem document, the list envelope, trimming and control-character rules, reference rules
(ADR-0008), MCP result mapping, tool metadata and argument rules — all as specs 001–004 define them. This spec
states business rules; section 10 carries one smoke criterion per inherited kind. Rule, edge-case and criterion
numbers are local; "002/R9" means rule R9 of spec 002.

## 1. Goal

Make stock real. After this spec a tenant can record that goods came into a warehouse or left it, and can ask
how much of an article is where — with the guarantee that the answer is always the sum of a history nobody can
rewrite.

- A **stock document** (`receipt` or `issue`) is prepared as a draft, freely edited, and then **posted**.
- Posting assigns the document number, writes **stock ledger entries** and makes the document immutable.
- **Stock on hand** per article and warehouse is the sum of the ledger entries, and never negative.
- Articles and warehouses that stock documents use can no longer be deleted, and a used article's `type` and
  base unit can no longer change.

## 2. Scope

In scope
1. `StockDocument` with lines, `StockLedgerEntry`, the per-tenant document-number counter; one migration.
2. Operations: list, get, get by number, create, replace, delete, **post**; stock-on-hand query; ledger query —
   each as an HTTP endpoint and an MCP tool.
3. Changes to existing operations: `DELETE /articles/{id}` and `DELETE /warehouses/{id}` -> `409 IN_USE` when
   used by a stock document; `PUT /articles/{id}` -> `409 IN_USE` when it would change `type` or `baseUnitId` of
   a used article. Their tools change with them.

Out of scope
- Transfers between warehouses, reversal / cancellation of a posted document (spec 006).
- Units other than the article's base unit on a line (007); stock count and opening-balance document (008).
- Partners, prices, costs and value on stock documents (009, 010, 011); reservations; lots, serials, bins.
- Configurable number series (013); accounting periods and date locking (012).
- Permissions to post (017) and the audit log (016): until then every tenant key may post, and attribution is
  `createdBy` / `updatedBy` / `postedBy` on the document and its entries.

## 3. Data

`StockDocument` (tenant-owned): `Id` uuid v7, `TenantId`, `Type` (`receipt` | `issue`), `Status`
(`draft` | `posted`), `Number` (null until posted), `DocumentDate` (date), `WarehouseId`, `Reference`
(nullable, max 100), `Note` (nullable, max 2000), `PostedAt`, `PostedBy` (both null until posted), audit columns.

`StockDocumentLine` (tenant-owned): `Id`, `TenantId`, `DocumentId`, `LineNo` (1…n), `ArticleId`,
`Quantity` numeric(18,6).

`StockLedgerEntry` (tenant-owned, append-only): `Id` uuid v7, `TenantId`, `ArticleId`, `WarehouseId`,
`Quantity` numeric(18,6) signed, `DocumentId`, `LineNo`, `DocumentDate`, `PostedAt`, `PostedBy`.

`DocumentCounter` (tenant-owned): `TenantId`, `DocumentType`, `LastNumber` — one row per tenant and type.

Keys: every foreign key between these tables and to `Article`, `Warehouse`, `ApiKey` includes `TenantId` and
restricts deletes (architecture §3, §8); lines are deleted with their draft by the application, not by cascade
from outside. Unique `(TenantId, Number)` on documents (where not null); unique `(TenantId, DocumentId, LineNo)`
on lines. Indexes that make "entries by article and warehouse" and "documents by warehouse" efficient.

## 4. Operations — HTTP

All routes are under `/api/v1` and require a tenant API key.

### 4.1 Stock documents

Representation (`StockDocument`):
```json
{ "id": "uuid", "type": "receipt", "status": "draft", "number": null,
  "documentDate": "2026-10-09",
  "warehouse": { "id": "uuid", "code": "WH-1", "name": "Main warehouse" },
  "reference": null, "note": null,
  "lines": [
    { "lineNo": 1,
      "article": { "id": "uuid", "code": "ART-001", "name": "Steel bolt M8" },
      "unit": { "id": "uuid", "code": "pcs", "name": "Piece" },
      "quantity": 100 } ],
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid",
  "postedAt": null, "postedBy": null }
```
`unit` is the article's base unit. In a list, each item is the same object **without** `lines` and with
`"lineCount": int`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /stock-documents?type=&status=&warehouseId=&search=&limit=&offset=` | `200` list envelope of document summaries | 400 |
| Get | `GET /stock-documents/{id}` | `200` StockDocument | 404 |
| Get by number | `GET /stock-documents/by-number/{number}` | `200` StockDocument | 404 |
| Create | `POST /stock-documents` body `{ "type", "documentDate", "warehouseId", "lines": [ { "articleId", "quantity" } ], "reference"?, "note"? }` | `201` StockDocument (`draft`), `Location` | 400, 409 `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `ARTICLE_NOT_STOCKED` |
| Replace | `PUT /stock-documents/{id}` body `{ "documentDate", "warehouseId", "reference", "note", "lines" }` (all five present) | `200` StockDocument | 400, 404, 409 `INVALID_STATE`, `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `ARTICLE_NOT_STOCKED` |
| Delete | `DELETE /stock-documents/{id}` | `204` | 404, 409 `INVALID_STATE` |
| **Post** | `POST /stock-documents/{id}/post` (no body) | `200` StockDocument (`posted`, with `number`, `postedAt`, `postedBy`) | 404, 409 `INVALID_STATE`, `REFERENCE_INACTIVE`, `INSUFFICIENT_STOCK` |

(401 / 403 as everywhere.)

### 4.2 Stock on hand

`GET /stock-on-hand?articleId=&warehouseId=&limit=&offset=` -> `200` list envelope of
```json
{ "article": { "id", "code", "name" }, "warehouse": { "id", "code", "name" },
  "unit": { "id", "code", "name" }, "quantity": 100 }
```
One item per (article, warehouse) pair whose quantity is not zero; a pair that is absent has zero stock.
Ordered by article code, then warehouse code (case-insensitive).

### 4.3 Stock ledger

`GET /stock-ledger-entries?articleId=&warehouseId=&documentId=&limit=&offset=` -> `200` list envelope of
```json
{ "id": "uuid", "article": { … }, "warehouse": { … }, "unit": { … }, "quantity": -40,
  "documentDate": "2026-10-09", "postedAt": "…Z", "postedBy": "uuid",
  "document": { "id": "uuid", "number": "SI-000001", "type": "issue" }, "lineNo": 1 }
```
Ordered by `postedAt`, then document number, then `lineNo`, ascending (oldest first).
There is no operation that creates, changes or deletes a ledger entry.

### 4.4 New error codes (registry: architecture §6)

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 409 | `INVALID_STATE` | Replace, delete or post of a document that is already `posted`. | — |
| 409 | `INSUFFICIENT_STOCK` | Posting would make stock on hand negative. | `lines[i].quantity` for every line of every short article |
| 409 | `ARTICLE_NOT_STOCKED` | A line names an article whose `type` is `service`. | `lines[i].articleId` |
| 409 | `IN_USE` *(extended)* | Also: a `PUT` would change `type` or `baseUnitId` of an article used by a stock document. | `type` and/or `baseUnitId` |

## 5. Operations — MCP

Implemented in this spec, by the rules of spec 003 (§5.2, §5.3, R12–R17) and the pattern of spec 004.

| Tool | Arguments | Success | HTTP operation | Error codes |
|---|---|---|---|---|
| `stock_document_list` | `{ type?, status?, warehouseId?, search?, limit?, offset? }` | list envelope | List | `VALIDATION_FAILED` |
| `stock_document_get` | `{ id?, number? }` — exactly one | StockDocument | Get / Get by number | `VALIDATION_FAILED`, `NOT_FOUND` |
| `stock_document_create` | `{ type, documentDate, warehouseId, lines, reference?, note? }` | StockDocument | Create | as HTTP |
| `stock_document_update` | `{ id, documentDate, warehouseId, reference, note, lines }` | StockDocument | Replace | as HTTP |
| `stock_document_delete` | `{ id }` | `{ "deleted": true }` | Delete | `NOT_FOUND`, `INVALID_STATE` |
| `stock_document_post` | `{ id }` | StockDocument | Post | `NOT_FOUND`, `INVALID_STATE`, `REFERENCE_INACTIVE`, `INSUFFICIENT_STOCK` |
| `stock_on_hand_list` | `{ articleId?, warehouseId?, limit?, offset? }` | list envelope | Stock on hand | `VALIDATION_FAILED` |
| `stock_ledger_entry_list` | `{ articleId?, warehouseId?, documentId?, limit?, offset? }` | list envelope | Stock ledger | `VALIDATION_FAILED` |

- `lines` is an array of `{ articleId: uuid, quantity: number }` objects (closed schema).
- Annotations: `*_list`, `*_get` read-only; `*_create`, `*_update`, `*_delete` by the pattern of 003 §5.3;
  `stock_document_post`: `readOnlyHint: false`, `destructiveHint: true`, `idempotentHint: false`,
  `openWorldHint: false`.
- The description of `stock_document_post` says that posting is permanent, what it does to stock, and what to
  do on `INSUFFICIENT_STOCK` (check `stock_on_hand_list`; the draft is unchanged and can be corrected).
- `tools/list` now returns exactly 32 tools: the 24 of spec 004 plus these 8. The literal tool-list test is
  updated by the tester (approved, as in 004 §5.3). `article_update`, `article_delete` and `warehouse_delete`
  can now return `IN_USE`; their descriptions say so.

## 6. Business rules

Document and lines
- R1. `type` is `"receipt"` (goods come into the warehouse) or `"issue"` (goods leave it). Required on create;
  it is not part of the replace body and never changes.
- R2. `documentDate` is required: a string `YYYY-MM-DD` that is a real calendar date. Any date is allowed,
  past or future (ADR-0012, decision 5).
- R3. `warehouseId` is required and follows the reference rules (ADR-0008): must exist in the tenant
  (`REFERENCE_NOT_FOUND`, key `warehouseId`); an inactive warehouse cannot be newly assigned
  (`REFERENCE_INACTIVE`).
- R4. `lines` is required: an array of 1 to 200 lines. Each line has exactly `articleId` and `quantity`.
  Lines are numbered 1…n in the order given; a replace renumbers them. The same article may appear on several
  lines.
- R5. `articleId` follows the reference rules: unknown -> `REFERENCE_NOT_FOUND`; inactive and newly assigned
  -> `REFERENCE_INACTIVE`; `type` `service` -> `ARTICLE_NOT_STOCKED`. All with key `lines[i].articleId`
  (`i` zero-based). An article is "newly assigned" on replace if the stored draft has no line with it.
- R6. `quantity` is a JSON number (a string is a wrong type), greater than 0, with at most 6 decimal places,
  at most 999999999.999999. It is in the article's base unit. Violations -> `400` with key
  `lines[i].quantity`. Quantities are exact decimals end to end: `0.1 + 0.2` is `0.3`.
- R7. `reference` (the user's own reference, e.g. a delivery-note number): optional single-line text, max 100.
  `note`: optional multi-line text, max 2000, as article `description` (002/R3). Both `null` when empty; both
  must be present on replace (ADR-0011, decision 7).
- R8. Order of checks on create and replace: validation (`400`, all fields and lines together) -> document
  exists (`404`) -> document is a draft (`INVALID_STATE`) -> warehouse reference -> line references, in line
  order, the first failing **kind** reported for all lines that have it (`REFERENCE_NOT_FOUND`, then
  `REFERENCE_INACTIVE`, then `ARTICLE_NOT_STOCKED`).

Lifecycle
- R9. A new document is a `draft`: `number`, `postedAt`, `postedBy` are `null`. A draft has no effect on stock
  on hand or on the ledger and reserves nothing.
- R10. A draft can be replaced and deleted any number of times. Deleting a draft removes it and its lines.
- R11. A `posted` document is immutable: replace, delete and a second post -> `409 INVALID_STATE`; the document
  is unchanged. (Reversal: spec 006.)

Posting
- R12. Posting is atomic. It succeeds completely — status `posted`, `number` assigned, `postedAt` = now,
  `postedBy` = the acting key, one ledger entry per line — or changes nothing at all.
- R13. Order of checks on post: document exists (`404`) -> is a draft (`INVALID_STATE`) -> warehouse and every
  article are active (`REFERENCE_INACTIVE`, keys `warehouseId` / `lines[i].articleId`) -> stock is sufficient
  (`INSUFFICIENT_STOCK`). Header before lines, as in R8: inactive masters named in the header are reported
  together and alone; only when the header is clean are inactive masters of lines reported, for all lines
  that have one (`005-q.md`, B-Q5). Every later posting and confirmation that says "masters are active"
  follows this.
- R14. Ledger entries: for a `receipt` line, `+quantity`; for an `issue` line, `−quantity`; article, warehouse
  and `documentDate` from the document, `lineNo` from the line, `postedAt` / `postedBy` from the posting. All
  entries of one document have the same `postedAt`.
- R15. **No negative stock.** For an `issue`, for every article on the document, the sum of its line quantities
  must not exceed the stock on hand of that article in the document's warehouse at the moment of posting.
  Otherwise `409 INSUFFICIENT_STOCK`, with `errors` keys `lines[i].quantity` for all lines of each short
  article. Stock in other warehouses does not count. The document date plays no part.
- R16. The rule holds under concurrency: of several postings that together would overdraw a pair, only as many
  succeed as the stock covers; stock on hand is never negative at any time.
- R17. **Numbering.** The number is `SR-` (receipt) or `SI-` (issue) followed by the counter value padded with
  zeros to at least 6 digits. Each tenant has one counter per type, starting at 1 and increasing by exactly 1
  with every successful posting of that type: numbers are unique and gapless per tenant and type, in posting
  order. Failed postings, drafts and deleted drafts consume nothing.
- R18. A receipt is never refused for stock reasons.

Stock on hand and ledger
- R19. Stock on hand of an (article, warehouse) pair = the sum of `quantity` over its ledger entries. This
  equality always holds; no other operation changes stock.
- R20. `GET /stock-on-hand` lists pairs with a non-zero sum. `articleId` / `warehouseId` filter when present;
  a well-formed id that matches nothing gives an empty list (002/R19).
- R21. Ledger filters `articleId`, `warehouseId`, `documentId` combine with AND; same rule for unknown ids.
- R22. Document list: `type` and `status` filter by exact value; `warehouseId` by warehouse; `search` matches
  case-insensitively as a substring of `number` or `reference`. Ordered by `createdAt` descending, then `id`
  descending (newest first). `by-number` lookup is case-insensitive.
- R23. Summaries (`warehouse`, `article`, `unit`) show the masters' current code and name, also on posted
  documents and ledger entries (ADR-0008).

Effects on masters
- R24. An article is **used** while at least one stock document line — of a draft or a posted document — names
  it. A warehouse is used while at least one stock document names it.
- R25. A used article or warehouse cannot be deleted: `409 IN_USE`. It can be deactivated, renamed and
  re-coded.
- R26. A used article's `type` and `baseUnitId` are frozen: a `PUT /articles/{id}` that changes either ->
  `409 IN_USE` with `errors` keys naming the changed frozen fields; nothing is changed. A `PUT` that keeps both
  values succeeds. The check comes after `404` and before the reference checks of 002/R13: a changed
  `baseUnitId` is `IN_USE` whatever it points at, also an unknown or inactive unit (`005-q.md`, T-Q3).
- R27. Deleting the last draft that uses an article or warehouse makes it unused again. Posted documents are
  never deleted, so an article or warehouse on a posted document stays used for good.
- R28. A unit of measure used as base unit of an article stays `IN_USE` as in spec 002 (unchanged).

## 7. Edge cases

- E1. `lines` missing, `null`, not an array, `[]`, or 201 lines -> `400` with key `lines`.
- E2. A line that is not an object, lacks `articleId` or `quantity`, or has an extra property (`"unitId"`,
  `"lineNo"`, `"price"`) -> `400` with a key that starts with `lines[i]`.
- E3. `quantity` `0`, `-1`, `"5"`, `null`, `1.0000001`, `1000000000` -> `400` with `lines[i].quantity`;
  `0.000001` and `999999999.999999` -> accepted.
- E4. `documentDate` missing, `""`, `"2026-02-30"`, `"09.10.2026"`, `"2026-10-09T10:00:00Z"` -> `400` with key
  `documentDate`. `"1999-12-31"` and `"2099-01-01"` -> accepted.
- E5. `type` missing, `"Receipt"`, `"transfer"` -> `400` with key `type`; `type` in a replace body -> `400`
  (unknown property).
- E6. Two lines with the same article on an issue, each within stock but together exceeding it ->
  `INSUFFICIENT_STOCK` with both lines' keys.
- E7. An issue with one line that is covered and one that is not -> `INSUFFICIENT_STOCK` with only the short
  line's key; nothing is posted, not even the covered line.
- E8. Issue of exactly the stock on hand -> posted; the pair disappears from `GET /stock-on-hand`.
- E9. A draft issue created while stock was sufficient, posted after another issue took the stock ->
  `INSUFFICIENT_STOCK`; the draft is unchanged and can be corrected and posted later.
- E10. An article or warehouse deactivated after the draft was saved: the draft can still be read, replaced
  (keeping that reference) and deleted; posting -> `REFERENCE_INACTIVE`; after reactivation posting succeeds.
- E11. Posting the same draft twice in parallel: exactly one `200`, the other `409 INVALID_STATE`; one number,
  one set of entries.
- E12. `by-number` with an unknown number, a draft's id as number, or a syntactically odd value ->
  `404 NOT_FOUND`.
- E13. An article used only by a draft: `DELETE` -> `IN_USE`; after the draft is deleted -> `204`.

## 8. Tenant isolation

- T1. All four new entities are tenant-owned (filter, stamping, write check); all foreign keys include
  `TenantId`. The counter is per tenant: tenants' numbers are independent and may coincide.
- T2. Another tenant's documents, entries and stock are non-existent: not listed, not counted, not summed,
  `404 NOT_FOUND` on get / get-by-number / replace / delete / **post**.
- T3. Another tenant's warehouse or article as a reference -> `REFERENCE_NOT_FOUND`, exactly as a random id;
  as a filter -> empty result.
- T4. Stock is computed only from the caller's tenant's entries. One tenant's postings never change another
  tenant's stock, numbers or `IN_USE` state.
- T5. The same through tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Tenant key required on every route and tool; admin key -> `403` (inherited).
- S2. Any tenant key, `human` or `agent`, may post (owner decision; restrictable from spec 017). `postedBy`
  makes every posting attributable to one key.
- S3. The ledger is append-only in the application: no operation, endpoint or tool updates or deletes an entry,
  and the DbContext refuses to save a modified or deleted `StockLedgerEntry` and a modified posted document.
- S4. Error bodies reveal no other tenant's data; `INSUFFICIENT_STOCK` may state the available quantity of the
  caller's own stock in `detail`, never in a contract field.
- S5. Parameterised queries only; `search` escaped as before.

## 10. Acceptance criteria

Conventions as in specs 001 §10, 003 §10 and 004 §10. Unmarked criteria are black-box (tester); *(builder)*
and *(manual)* as in architecture §9. Quantities are compared numerically (`10` equals `10.000000`).

Setup used below ("the standard setup"): a new tenant with unit `pcs`, stock articles `A` and `B` and service
article `S` on that unit, and warehouses `W1` and `W2` — all created through the API. "Receive n of A into W1"
means: create a receipt for `W1` with one line (A, n) and post it. "Stock(A, W1)" is the `quantity` of that
pair in `GET /stock-on-hand?articleId=A&warehouseId=W1`, or 0 when the list is empty.

Structure
- AC-01 *(manual)* `scripts/dotnet.sh build` and `test` exit 0; earlier tests pass unweakened; the only earlier
  tests changed are the literal tool list and tests pinning the table list. One migration added.
- AC-02 *(builder, model)* The model tests of 001/AC-06 and 002/AC-04 pass unmodified with the four new
  entities.
- AC-03 *(builder)* Saving a modified or deleted `StockLedgerEntry`, or a modified line or header of a posted
  document, through the DbContext fails and changes nothing (S3).
- AC-04 *(builder, unit)* The sufficiency rule (R15) and the numbering rule (R17, including 7-digit values)
  are unit-tested without HTTP.

Inherited behaviour — one smoke test per kind
- AC-10 `GET /stock-documents`, `POST /stock-documents`, `POST /stock-documents/{uuid}/post`,
  `GET /stock-on-hand`, `GET /stock-ledger-entries` without a credential -> `401` `UNAUTHENTICATED`; with the
  admin key -> `403` `FORBIDDEN`.
- AC-11 `POST /stock-documents` with an unknown property (`"status": "posted"`, `"number": "X"`, `"tenantId"`)
  -> `400` `VALIDATION_FAILED`; `GET /stock-on-hand?foo=1` and `GET /stock-documents?status=open` -> `400` with
  `errors` key `foo` / `status`.

Drafts
- AC-20 `POST /stock-documents` `{ "type": "receipt", "documentDate": "2026-10-09", "warehouseId": W1,
  "lines": [ { "articleId": A, "quantity": 100 } ] }` -> `201`; `status == "draft"`, `number`, `postedAt`,
  `postedBy`, `reference`, `note` all `null` (present); `warehouse.id == W1` with W1's code and name; one line
  with `lineNo == 1`, `article.id == A` with A's code and name, `unit.code == "pcs"`, `quantity == 100`;
  `createdBy ==` the acting key; `Location` ends with `/api/v1/stock-documents/{id}`; `GET /{id}` returns the
  same.
- AC-21 After AC-20, Stock(A, W1) is 0, `GET /stock-on-hand` and `GET /stock-ledger-entries` are empty.
- AC-22 A document with lines (A, 1), (B, 2.5), (A, 0.000001) -> `201`; lines numbered 1, 2, 3 in that order
  with those quantities.
- AC-23 `PUT /{id}` on a draft with another date, `W2`, a reference, a note and lines [(B, 7)] -> `200` with
  the new values and exactly one line (`lineNo == 1`, B, 7); `type` and `id` unchanged; `status == "draft"`.
- AC-24 `PUT` omitting `documentDate`, `warehouseId`, `reference`, `note` or `lines` (one at a time) -> `400`
  with that key; with `"type"` in the body -> `400`. The draft is unchanged.
- AC-25 `DELETE /{id}` on a draft -> `204`; `GET /{id}` -> `404`.
- AC-26 Validation (each `400` `VALIDATION_FAILED` with the stated key, nothing created): `lines` missing or
  `[]` -> `lines`; 201 lines -> `lines`; quantity `0`, `-1`, `"5"`, `1.0000001`, `1000000000` on the second
  line -> `lines[1].quantity`; a line without `articleId` -> `lines[0].articleId`; `documentDate`
  `"2026-02-30"` or `"09.10.2026"` -> `documentDate`; `type` `"transfer"` or missing -> `type`;
  `warehouseId` missing or `"abc"` -> `warehouseId`. A document with 200 lines and one with quantity
  `999999999.999999` -> `201`.
- AC-27 References on create: `warehouseId` random UUID -> `409` `REFERENCE_NOT_FOUND` with key `warehouseId`;
  inactive warehouse -> `409` `REFERENCE_INACTIVE` with key `warehouseId`; second line with a random article
  id -> `REFERENCE_NOT_FOUND` with key `lines[1].articleId`; with an inactive article -> `REFERENCE_INACTIVE`
  with key `lines[1].articleId`; with service article `S` -> `409` `ARTICLE_NOT_STOCKED` with key
  `lines[1].articleId`. Nothing is created.
- AC-28 A draft on W1 with A; W1 and A are then deactivated. `PUT` keeping W1 and A with another quantity ->
  `200`. `PUT` adding inactive article B (deactivated too) -> `409` `REFERENCE_INACTIVE` with that line's key.

Posting a receipt
- AC-30 `POST /{id}/post` on the draft of AC-20 -> `200`; `status == "posted"`, `number == "SR-000001"`,
  `postedAt` set, `postedBy ==` the acting key; lines unchanged. `GET /{id}` and
  `GET /stock-documents/by-number/SR-000001` (and `sr-000001`) return the same document.
- AC-31 After AC-30: Stock(A, W1) is 100; `GET /stock-on-hand` has exactly one item, with A's, W1's and the
  unit's summaries; `GET /stock-ledger-entries` has exactly one entry with `quantity == 100`, `article.id == A`,
  `warehouse.id == W1`, `documentDate == "2026-10-09"`, `document.id`, `document.number == "SR-000001"`,
  `document.type == "receipt"`, `lineNo == 1`, `postedBy ==` the acting key, and `postedAt` equal to the
  document's.
- AC-32 A receipt with lines (A, 1), (B, 2), (A, 3) posted -> three ledger entries (+1, +2, +3, `lineNo` 1–3,
  same `postedAt`); Stock(A, W1) is 4, Stock(B, W1) is 2.
- AC-33 A draft created by key K1 and posted by key K2 (from `POST /api/v1/api-keys`) -> `createdBy == K1`,
  `postedBy == K2`, and the ledger entry's `postedBy == K2`.
- AC-34 Posted is immutable: `PUT` (valid body), `DELETE` and a second `POST /post` -> `409`, problem with code
  `INVALID_STATE`; the document, stock and ledger are unchanged.
- AC-35 Receipts into W1 and W2 keep separate stock: receive 5 of A into W1 and 7 into W2 -> Stock(A, W1) is 5,
  Stock(A, W2) is 7; `GET /stock-on-hand?articleId=A` has two items ordered by warehouse code.
- AC-36 Exact decimals: receive 0.1 and then 0.2 of A into W1 -> Stock(A, W1) equals 0.3 exactly; an issue of
  0.3 posts; Stock(A, W1) is 0 and the pair is no longer listed.
- AC-37 A receipt dated `1999-12-31` and one dated `2099-01-01` both post; their ledger entries carry those
  `documentDate`s.

Posting an issue — no negative stock
- AC-40 With 100 of A in W1: an issue of 40 posts -> `number == "SI-000001"`; ledger entry `quantity == -40`;
  Stock(A, W1) is 60.
- AC-41 Then an issue of 61 -> `409`, problem with code `INSUFFICIENT_STOCK`, `errors` has key
  `lines[0].quantity`; the document is still a `draft` with `number == null`; Stock(A, W1) is 60; the ledger
  has no new entry. After `PUT` changes the quantity to 60, posting succeeds and Stock(A, W1) is 0.
- AC-42 Issue from a warehouse where the article was never received (A in W2, while W1 has stock) ->
  `INSUFFICIENT_STOCK`.
- AC-43 With 10 of A in W1: an issue with lines (A, 6), (A, 6) -> `INSUFFICIENT_STOCK` with keys
  `lines[0].quantity` and `lines[1].quantity`. An issue with lines (A, 6), (A, 4) posts.
- AC-44 With 10 of A and 0 of B in W1: an issue with lines (A, 5), (B, 1) -> `INSUFFICIENT_STOCK` with key
  `lines[1].quantity` and without `lines[0].quantity`; Stock(A, W1) is still 10 (nothing was posted).
- AC-45 A draft issue of 10 while stock is 10; a second issue of 10 is created and posted first; posting the
  first -> `INSUFFICIENT_STOCK`. After receiving 10 more, posting the first succeeds.
- AC-46 Race: with exactly 10 of A in W1 and 10 draft issues of 3 each, all ten are posted in parallel ->
  exactly three `200` and seven `409 INSUFFICIENT_STOCK`, no other status; Stock(A, W1) is 1; the ledger has
  exactly three issue entries; the three numbers are `SI-000001`…`SI-000003`.
- AC-47 The document date plays no part: with stock received today, an issue dated `2000-01-01` posts; with no
  stock, an issue dated `2099-01-01` -> `INSUFFICIENT_STOCK`.
- AC-48 Posting needs active masters: a draft whose warehouse was deactivated -> `409` `REFERENCE_INACTIVE`
  with key `warehouseId`; a draft whose second article was deactivated -> `REFERENCE_INACTIVE` with key
  `lines[1].articleId`; the draft stays a draft. After reactivation (`PUT` on the master) posting succeeds.

Numbering
- AC-50 Three receipts posted one after another get `SR-000001`, `SR-000002`, `SR-000003`; the first issue
  posted afterwards gets `SI-000001`.
- AC-51 Failed postings and drafts consume no number: after `SI-000001`, an issue that fails with
  `INSUFFICIENT_STOCK`, a draft that is deleted and a draft left unposted, the next posted issue is
  `SI-000002`.
- AC-52 Ten receipts posted in parallel -> ten `200`; their numbers are exactly the set
  `SR-000001`…`SR-000010`.
- AC-53 Posting the same draft twice in parallel -> one `200` and one `409 INVALID_STATE`; the ledger has one
  set of entries for the document; the next posted receipt gets the next number without a gap.

Queries
- AC-60 Ledger equals stock: after a sequence of at least six postings (receipts and issues of A and B in W1
  and W2), for every pair the sum of `quantity` over `GET /stock-ledger-entries?articleId=&warehouseId=` equals
  Stock(pair); pairs whose sum is 0 are absent from `GET /stock-on-hand`.
- AC-61 Ledger order and filters: entries are returned oldest first; `documentId=` returns exactly that
  document's entries in `lineNo` order; `articleId=` and `warehouseId=` combine; a random UUID in any filter
  -> `200`, empty; `articleId=abc` -> `400` with key `articleId`.
- AC-62 `GET /stock-on-hand` orders by article code then warehouse code; `warehouseId=W2` returns only W2's
  pairs; `limit=1&offset=1` returns the second item with the full `total`.
- AC-63 Document list: newest first; each item has `lineCount` and no `lines`; `type=issue`, `status=draft`,
  `warehouseId=W2` filter and combine; `search` matches a posted document's number (`sr-0000`) and a draft's
  `reference`, case-insensitively; `type=transfer` -> `400` with key `type`.
- AC-64 After article A is renamed and re-coded (`PUT /articles`), the posted document's line, the ledger
  entry and the stock-on-hand item show A's new code and name and the same id; quantities are unchanged.

Effects on masters
- AC-70 An article on a draft line: `DELETE /articles/{id}` -> `409` `IN_USE`. After the draft is deleted ->
  `204`.
- AC-71 An article on a posted document: `DELETE` -> `409` `IN_USE`; `PUT` changing only `name`, `code`,
  `description` or `isActive` -> `200`.
- AC-72 A used article (draft or posted): `PUT` changing `type` to `service` -> `409` `IN_USE` with `errors`
  key `type`; changing `baseUnitId` to another active unit -> `409` `IN_USE` with key `baseUnitId`; changing
  both -> both keys; the article is unchanged. An unused article can still change both (`200`).
- AC-73 A warehouse on a draft or posted document: `DELETE /warehouses/{id}` -> `409` `IN_USE`; `PUT`
  (rename, deactivate) -> `200`. An unused warehouse -> `204`.
- AC-74 `GET /stock-documents/{random UUID}`, `…/by-number/SR-999999`, `PUT`, `DELETE` and `POST /post` on a
  random UUID -> `404` `NOT_FOUND`.

MCP
- AC-80 `tools/list` returns exactly 32 names: the 24 of spec 004 and the 8 of section 5 (literal list).
  Each new tool has a description, a closed `inputSchema` with described properties and `required` as in
  section 5, an `outputSchema`, and its annotations; `stock_document_post` has `destructiveHint == true` and
  `readOnlyHint == false`.
- AC-81 Full flow through tools only: `stock_document_create` (receipt, A, 100) -> tool success, `draft`;
  `stock_document_post` -> tool success, `number == "SR-000001"`, `postedBy ==` the MCP key;
  `stock_on_hand_list` `{ "articleId": A }` -> one item, quantity 100; `stock_ledger_entry_list`
  `{ "documentId": … }` -> one entry, +100. Each result is equal to HTTP for the corresponding request.
- AC-82 `stock_document_get` by `id` and by `number`; `stock_document_list` with
  `{ "type": "receipt", "status": "posted" }`; `stock_document_update` on a draft; `stock_document_delete` on
  a draft (`{ "deleted": true }`) -> tool success, equal to HTTP.
- AC-83 Tool errors, each with the same `code` and `errors` keys as the same input over HTTP:
  `stock_document_post` of an issue beyond stock -> `INSUFFICIENT_STOCK` (`lines[0].quantity`);
  `stock_document_post` / `_update` / `_delete` of a posted document -> `INVALID_STATE`;
  `stock_document_create` with service article -> `ARTICLE_NOT_STOCKED` (`lines[0].articleId`); with quantity
  `0` -> `VALIDATION_FAILED` (`lines[0].quantity`); with quantity `"5"` -> `VALIDATION_FAILED` (key `lines` or
  `lines[0].quantity`, not asserted; `005-q.md`, T-Q6);
  `stock_document_get` with `{}` -> `VALIDATION_FAILED` (`id`, `number`); `article_delete` of a used article
  -> `IN_USE`.
- AC-84 A draft created over HTTP by key H and posted through `stock_document_post` by key M has
  `createdBy == H`, `postedBy == M`.

Tenant isolation (tenants X and Y, each with the standard setup, its own key and MCP client)
- AC-90 X receives 100 of its A into its W1. Y: `GET /stock-on-hand`, `GET /stock-ledger-entries`,
  `GET /stock-documents` -> empty, `total == 0`, also when filtered by X's article, warehouse and document ids;
  the same through `stock_on_hand_list`, `stock_ledger_entry_list`, `stock_document_list`.
- AC-91 Y: `GET`, `PUT` (valid body), `DELETE` and `POST /post` on X's draft id and on X's posted document id,
  `GET by-number/SR-000001` before Y has posted anything -> `404` `NOT_FOUND`; through tools -> tool error
  `NOT_FOUND`. X's documents and stock are unchanged; X's draft is still a draft.
- AC-92 Y: a document with `warehouseId` = X's warehouse -> `409` `REFERENCE_NOT_FOUND` (key `warehouseId`);
  with X's article on a line -> `REFERENCE_NOT_FOUND` (key `lines[0].articleId`); same through
  `stock_document_create`.
- AC-93 Numbers are per tenant: Y's first posted receipt is `SR-000001` although X already has one; each
  tenant's `by-number/SR-000001` returns its own document.
- AC-94 Stock is per tenant: X has 100 of an article with code `A`; Y's article with the same code has stock
  0, and Y's issue of 1 -> `INSUFFICIENT_STOCK`. X's stock is still 100.
- AC-95 Y's documents never make X's masters used: Y creates drafts on its own `A` and `W1`; X can delete an
  unused article and warehouse of its own with the same codes (`204`).

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on sections "Posting", "Numbering", "Queries" and "Effects on masters". Do not re-test
  inherited transport behaviour beyond AC-10 and AC-11.
- Compare quantities as decimals, not as strings or doubles. Numbers and stock are per tenant, so a fresh
  tenant per test gives deterministic numbers (`SR-000001`).
- AC-46, AC-52 and AC-53 are the concurrency criteria; send the requests truly in parallel.

Builder
- Posting must be one database transaction that (a) locks the document row, (b) locks what guards stock for
  the affected (article, warehouse) pairs in a fixed order, (c) locks the counter row, (d) writes. A
  check-then-write without locks will fail AC-46 and AC-52; a database sequence will fail AC-51.
  **Accepted for the MVP (ADR-0012, amendment of 2026-10-09; `005-q.md`, B-Q2):** one lock per tenant held
  by every such write replaces (a)–(c). The lock notes of specs 006, 008, 009 and 010 are satisfied by it
  the same way.
- The sufficiency rule and the numbering format are Domain/Application code (AC-04); the endpoint and the tool
  only call `Post`.
- Stock on hand may be a `SUM` over the ledger or a maintained projection; if a projection, it is written in
  the posting transaction and AC-60 must hold.
- Use `decimal` end to end and `numeric(18,6)` in the database; make the JSON reader reject a quoted quantity
  and the validator reject more than 6 decimal places rather than rounding.
- The "used" checks (R24–R27) extend the `IN_USE` mechanism of spec 002, including translation of the
  database's foreign-key refusal for the raced case.
- Design the document/lines/post/ledger pieces so that spec 006 can add a `transfer` type (second warehouse,
  two entries per line) and reversal without reshaping them; do not build either now.
- Anything unclear or contradictory: `docs/questions/005-q.md`, then continue with the rest.
