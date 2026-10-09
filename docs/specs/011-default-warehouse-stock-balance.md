# Spec 011 — Default warehouse, warehouse stock list and the stored stock balance

Status: ready for the tester and the builder. The MVP (specs 001–010) is merged; this is the first spec after it,
asked for by the owner on 2026-10-10.
Branches: `tests/011-default-warehouse-stock-balance` (tester), `feat/011-default-warehouse-stock-balance`
(builder).
Read first: specs **004** (warehouses), **005** (ledger, posting, stock on hand), 006, 008, 009 and 010
(the documents that name a warehouse), ADR-0012 with its amendment (the per-tenant lock), **ADR-0018** (the
stored balance) and **ADR-0019** (the default warehouse; a warehouse on every document).
Why this is the eleventh spec: `docs/roadmap.md` section 3, row 11.

**Inherited, not re-specified** (roadmap §4): everything specs 001–010 established — transport, errors,
references, MCP mapping, masters, stock documents, posting, reversal, units, counts, numbering, orders. This
spec states only what is new or changed. Numbers are local; "005/R15" means rule R15 of spec 005.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

The owner's request, in three parts:

- **Every tenant has warehouses, and one from the first moment.** A tenant is created together with a
  *central warehouse*, which is its **default warehouse**. A tenant always has exactly one default warehouse
  and it is always usable.
- **Every document is on a warehouse.** A receipt, an issue, a count, an order — and every document type added
  later, invoices included — names exactly one warehouse (a transfer, two). A caller who does not name one
  gets the default warehouse.
- **Every warehouse has a stock list** ("lager lista"): all stock articles of the tenant with their quantity
  in that warehouse, zero included. The quantity is **stored** in the database, per warehouse and article —
  and yet it is never the truth: the ledger written by posted documents is. The stored quantity always equals
  the sum of the ledger, can be verified against it and can be rebuilt from it at any time.

## 2. Scope

In scope
1. `Warehouse.isDefault`; the default warehouse created with the tenant; the operation that makes another
   warehouse the default; the rules that protect the default warehouse.
2. `warehouseId` optional on **create** of a stock document (receipt, issue, count) and of an order.
3. The stock list of one warehouse: operation and tool.
4. The table `StockBalance`, written with the ledger; the operations **verify** (list of differences) and
   **rebuild**.
5. One migration that gives every existing tenant a default warehouse and fills `StockBalance` from the
   ledger.

Out of scope
- Any change to what a posting does to stock, to numbering, to orders or to the ledger itself.
- Value of stock (012); bins or locations inside a warehouse; a default warehouse per API key, per user or per
  partner; per-warehouse settings.
- A balance per date ("stock as of"), a balance history, snapshots: the ledger answers those.
- Invoices (015). This spec only fixes the rule they will follow (R12).
- Narrowing the per-tenant lock (ADR-0012 amendment); it stays as built.

## 3. Data

`Warehouse` gains `IsDefault` (bool, not null). A partial unique index allows at most one row with
`IsDefault = true` per tenant; a check constraint refuses `IsDefault = true` together with `IsActive = false`.

`StockBalance` (tenant-owned, **derived**): `TenantId`, `WarehouseId`, `ArticleId`, `Quantity` numeric(18,6).
- Primary key `(TenantId, WarehouseId, ArticleId)`. Foreign keys `(TenantId, WarehouseId)` -> `Warehouse` and
  `(TenantId, ArticleId)` -> `Article`, `ON DELETE RESTRICT` (architecture §3).
- Check constraint `Quantity >= 0`: the database itself refuses negative stock.
- No audit columns: a row is a sum, not a record somebody made; who moved stock and when is in the ledger
  (ADR-0018). No row means quantity 0; whether a pair whose quantity returned to 0 keeps its row is not part
  of the contract.

One migration (section 6, R29–R31). Earlier migrations are not edited.

## 4. Operations — HTTP

All routes are under `/api/v1` and require a tenant API key.

### 4.1 Warehouses

The `Warehouse` representation (004 §4.2) gains `"isDefault": bool`, always present. Embedded warehouse
summaries (`{ "id", "code", "name" }`) are unchanged.

| Operation | Change |
|---|---|
| List | New filter `isDefault=true\|false`. `GET /warehouses?isDefault=true` returns exactly one warehouse. |
| Create | Unchanged body; the new warehouse has `isDefault == false`. `isDefault` in the body is an unknown property (`400`). |
| Replace | Unchanged body of nine fields; `isDefault` in the body is an unknown property (`400`). New error: `409 DEFAULT_WAREHOUSE` when `isActive: false` is sent for the default warehouse. |
| Delete | New error: `409 DEFAULT_WAREHOUSE` for the default warehouse. |
| **Set default** *(new)* | `POST /warehouses/{id}/set-default` (no body) -> `200` Warehouse with `isDefault == true`. Errors: `404`, `409 DEFAULT_WAREHOUSE` (the warehouse is inactive). |

`POST /api/v1/admin/tenants` keeps its request and its response (001 §4.1); it now also creates the default
warehouse (R1).

### 4.2 Documents

| Operation | Change |
|---|---|
| `POST /stock-documents` | `warehouseId` may be omitted or `null` when `type` is `receipt`, `issue` or `count` (R8–R10). For `transfer` it stays required. |
| `POST /purchase-orders`, `POST /sales-orders` | `warehouseId` may be omitted or `null` (R8). |
| `PUT` of any of them | Unchanged: `warehouseId` must be present and name a warehouse. |

Every representation still shows the document's `warehouse`; a document never has `warehouse == null`.

### 4.3 Stock list of a warehouse

`GET /warehouses/{id}/stock?search=&isActive=&hasStock=&limit=&offset=` -> `200` list envelope of
```json
{ "article": { "id": "uuid", "code": "ART-001", "name": "Steel bolt M8" },
  "warehouse": { "id": "uuid", "code": "CENTRAL", "name": "Central warehouse" },
  "unit": { "id": "uuid", "code": "pcs", "name": "Piece" },
  "quantity": 0, "incomingQuantity": 0, "reservedQuantity": 0, "availableQuantity": 0 }
```
The item has exactly the properties of a stock-on-hand item (005 §4.2, 009, 010). One item per **stock
article of the tenant** — whether or not it ever moved in this warehouse. Errors: `400`, `404` (no such
warehouse in this tenant).

### 4.4 Stock on hand

`GET /stock-on-hand` is unchanged in request, response and meaning (R18).

### 4.5 Stock balance — verify and rebuild

| Operation | Request | Success | Errors |
|---|---|---|---|
| Verify | `GET /stock-balance-differences?limit=&offset=` | `200` list envelope of differences; `total == 0` means every stored balance equals the ledger | 400 |
| Rebuild | `POST /stock-balances/rebuild` (no body) | `200` `{ "pairs": int, "corrected": int }` | — |

A difference:
```json
{ "article": { … }, "warehouse": { … }, "unit": { … },
  "storedQuantity": 12, "ledgerQuantity": 10, "differenceQuantity": 2 }
```
There is no operation that sets, changes or deletes a stored balance.

### 4.6 New error code (registry: architecture §6)

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 409 | `DEFAULT_WAREHOUSE` | The operation would leave the tenant without an active default warehouse: delete of the default warehouse; replace of it with `isActive: false`; set-default of an inactive warehouse. | `isActive` (replace, set-default); none (delete) |

## 5. Operations — MCP

Implemented with this spec, by the rules of spec 003 and the pattern of spec 004.

| Tool | Arguments | Success | HTTP operation | Error codes |
|---|---|---|---|---|
| `warehouse_set_default` | `{ id }` | Warehouse | Set default | `NOT_FOUND`, `DEFAULT_WAREHOUSE` |
| `warehouse_stock_list` | `{ id, search?, isActive?, hasStock?, limit?, offset? }` | list envelope | Stock list | `VALIDATION_FAILED`, `NOT_FOUND` |
| `stock_balance_difference_list` | `{ limit?, offset? }` | list envelope | Verify | `VALIDATION_FAILED` |
| `stock_balance_rebuild` | `{}` | `{ pairs, corrected }` | Rebuild | — |

- Annotations: the two lists read-only. `warehouse_set_default`: `readOnlyHint: false`,
  `destructiveHint: false`, `idempotentHint: true`. `stock_balance_rebuild`: `readOnlyHint: false`,
  `destructiveHint: false`, `idempotentHint: true`. All `openWorldHint: false`.
- Changed tools: `warehouse_list` gains `isDefault?` (boolean); the output schema of every tool that returns
  a Warehouse gains `isDefault`. In `stock_document_create`, `purchase_order_create` and `sales_order_create`,
  `warehouseId` leaves `required` and allows a uuid or `null`.
- Descriptions say:
  - `warehouse_list` — the tenant's default warehouse is the one with `isDefault`; find it with
    `{ "isDefault": true }`.
  - `stock_document_create`, `purchase_order_create`, `sales_order_create` — without `warehouseId` the
    document is created on the tenant's default warehouse (on a document linked to an order: on the order's
    warehouse); a transfer must name both warehouses; the result shows which warehouse was taken.
  - `warehouse_update`, `warehouse_delete` — `DEFAULT_WAREHOUSE`: make another warehouse the default first
    (`warehouse_set_default`).
  - `warehouse_stock_list` — every stock article with its quantity in this warehouse, zero included; for
    stock across warehouses use `stock_on_hand_list`.
  - `stock_balance_difference_list` — an empty list is the healthy state; a difference is a defect of the
    system, not of the caller's data: run `stock_balance_rebuild` and report it.
  - `stock_balance_rebuild` — recomputes the stored quantities from the ledger; changes no document and no
    ledger entry; safe to repeat.
- `tools/list` returns exactly 57 tools: the 53 of spec 010 and these 4.

## 6. Business rules

The default warehouse
- R1. **A tenant is created with its default warehouse.** `POST /admin/tenants` creates, in one transaction,
  the tenant, its first API key and a warehouse with `code` `CENTRAL`, `name` `Central warehouse`, no
  address, `isActive == true`, `isDefault == true`, `createdBy == updatedBy ==` the first key. Either all
  three exist afterwards or none. *(default: code and name)*
- R2. **Invariant: at every moment a tenant has exactly one warehouse with `isDefault == true`, and that
  warehouse is active.** No sequence of operations, serial or parallel, breaks it.
- R3. Apart from R4–R6 the default warehouse is an ordinary warehouse: it can be renamed, re-coded and given
  an address with `PUT`; documents use it like any other; other warehouses are created, changed and deleted
  as in spec 004. The code `CENTRAL` has no meaning of its own — only `isDefault` has. *(default)*
- R4. The default warehouse cannot be deactivated: `PUT` with `isActive: false` -> `409 DEFAULT_WAREHOUSE`
  with key `isActive`; nothing changes. Order of checks: validation -> `404` -> `DEFAULT_WAREHOUSE` ->
  `CODE_TAKEN`. *(default)*
- R5. The default warehouse cannot be deleted: `409 DEFAULT_WAREHOUSE`, whether or not anything uses it
  (checked before `IN_USE`). *(default)*
- R6. **Changing the default.** `POST /warehouses/{id}/set-default` makes that warehouse the default and, in
  the same transaction, the former default an ordinary warehouse. The target must be active, otherwise
  `409 DEFAULT_WAREHOUSE` with key `isActive`. On the warehouse that already is the default it succeeds and
  changes nothing (`updatedAt` included). Otherwise both warehouses get `updatedAt` / `updatedBy` of the
  call. *(default: the tenant may change it)*
- R7. A former default warehouse is ordinary again: it can be deactivated, and deleted if unused. Changing
  the default changes no existing document: a document keeps the warehouse it has.

A warehouse on every document
- R8. **Every document has exactly one warehouse** (a transfer: a source and a destination). On **create** of
  a receipt, an issue, a count, a purchase order or a sales order, `warehouseId` omitted or `null` means *the
  tenant's default warehouse at that moment*. The warehouse is resolved once, stored, and shown in the
  response; from then on the document is exactly as if the caller had named it. *(default)*
- R9. Omission is offered on create only. `PUT` replaces the whole document and must name the warehouse, as
  before (`400` with key `warehouseId` when missing or `null`). A transfer names both of its warehouses:
  `warehouseId` missing or `null` on a transfer -> `400` with key `warehouseId`, as before. *(default)*
- R10. On a stock document created **with** `purchaseOrderId` or `salesOrderId`, an omitted `warehouseId`
  means *the order's warehouse* — the only warehouse the link allows (009/R21, `ORDER_MISMATCH`). The order is looked up first:
  an unknown order is `REFERENCE_NOT_FOUND` with key `purchaseOrderId` / `salesOrderId` alone. All other
  checks of 009/010 follow as if the caller had named the order's warehouse. *(default)*
- R11. A `warehouseId` that is present and not `null` follows the rules of specs 005–010 unchanged: wrong
  form -> `400`; unknown -> `REFERENCE_NOT_FOUND`; inactive -> `REFERENCE_INACTIVE`.
- R12. **Rule for every later document type** (ADR-0019): its header carries a `warehouseId`, stored as a
  mandatory reference, with R8 on create and the reference rules of ADR-0008. Invoices (015) are the first
  to which this applies. A spec that wants a document without a warehouse needs the owner's decision.

The stock list
- R13. The stock list of a warehouse has one item per article of the tenant whose `type` is `stock` —
  active or not, used or not. `service` articles are never on it. `total` is the number of such articles
  that pass the filters.
- R14. For every item, the four quantities are those of the pair (article, this warehouse) as stock on hand
  defines them (005/R19, 009, 010/R15–R16), and `0` where stock on hand would not list the pair. An item
  whose pair is listed by `GET /stock-on-hand` is JSON-equal to that item.
- R15. Ordered by article code, case-insensitive, ascending. `search` and `isActive` are those of
  `GET /articles` (002/R18): `search` matches the article's `code` or `name`; `isActive` is the article's.
  `hasStock=true` keeps the items with `quantity > 0`, `hasStock=false` those with `quantity == 0`. Filters
  combine with AND. `limit` / `offset` as everywhere.
- R16. The warehouse is the addressed record: an unknown id, another tenant's id or a malformed id ->
  `404 NOT_FOUND`. An inactive warehouse has a stock list like any other.
- R17. The list is a read: it changes nothing and writes no balance rows.
- R18. `GET /stock-on-hand` stays the list of (article, warehouse) pairs with something to show, across
  warehouses (010/R19). The stock list is the complete list for one warehouse. Both show the same numbers
  from the same source.

The stored balance
- R19. **The ledger is the truth; the balance is derived from it.** Posted documents produce ledger entries
  (005/R14, 006, 008); the ledger is append-only. The stored balance of an (article, warehouse) pair is a
  copy of one number: the sum of `quantity` over the pair's ledger entries.
- R20. **Invariant: for every pair, stored balance == sum of the ledger** (a pair without a row counts as 0,
  a pair without entries as 0) — after every committed operation, also under parallel postings, and never
  negative.
- R21. The balance is written in exactly two places: (a) wherever ledger entries are written — posting and
  reversal of every stock document type — in the **same transaction** and under the **same per-tenant lock**
  (ADR-0012 amendment), each entry adding its signed quantity to its pair; (b) rebuild (R25). Nothing else
  writes it: no endpoint, no tool, no draft, no order, no change of a master.
- R22. A posting that is refused or fails leaves the balance exactly as it was — as it leaves the ledger and
  the counter (005/R12).
- R23. **Every quantity "on hand" the system shows or decides on is the stored balance**: `quantity` in stock
  on hand and in the stock list (and through it `availableQuantity`), the sufficiency check of an issue, a
  transfer and a reversal (005/R15, 006), the book quantity of a count and its "current" check (008/R6,
  R10). By R20 this changes no result of specs 005–010.

Verify and rebuild
- R24. **Verify.** `GET /stock-balance-differences` lists every pair of the tenant whose stored balance
  differs from the sum of its ledger entries, with `storedQuantity`, `ledgerQuantity` and
  `differenceQuantity` = stored − ledger; ordered by article code, then warehouse code. It compares both
  sides as of one moment: a posting in progress is seen either wholly or not at all, so verification never
  reports a difference that a concurrent posting explains. It changes nothing.
- R25. **Rebuild.** `POST /stock-balances/rebuild` replaces the stored balances of the caller's tenant by the
  sums of the ledger, in one transaction under the per-tenant lock. It reads the ledger and writes only
  `StockBalance`: no document, entry, number or audit field changes. `pairs` is the number of pairs that have
  ledger entries; `corrected` the number of pairs whose stored quantity differed from the ledger before the
  rebuild. Immediately after it, verify has `total == 0`.
- R26. In a system without defects, verify is always empty and rebuild always returns `corrected == 0`,
  whatever was posted before and whatever is being posted meanwhile. Rebuild may be called any number of
  times.
- R27. Any tenant key may verify and rebuild (restrictable from spec 018). *(default)*
- R28. Concurrency: postings, reversals, rebuild and set-default of one tenant are serialised by the
  per-tenant lock; the result is always that of some order of them. Set-default and the replace or delete of
  a warehouse are serialised against each other so that R2 holds.

Migration
- R29. Every tenant that exists when the migration runs gets a default warehouse: its **oldest active
  warehouse** (by `CreatedAt`, then `Id`) is marked `IsDefault`; a tenant with no active warehouse gets a new
  one as in R1 — code `CENTRAL`, or `CENTRAL-2`, `CENTRAL-3`, … if that code is taken — with `CreatedBy` the
  tenant's oldest API key. *(default)*
- R30. `StockBalance` is filled for every tenant with the sum of the ledger per pair. After the migration
  R2 and R20 hold for every tenant.
- R31. No existing document changes: every stock document and order already has a warehouse.

## 7. Edge cases

- E1. A new tenant: `GET /warehouses` has exactly one item, `CENTRAL`, `isDefault == true`; a receipt without
  `warehouseId` can be created and posted before any warehouse was created by hand.
- E2. `POST /warehouses` with code `central` in a new tenant -> `409 CODE_TAKEN` (the default has it). After
  the default is re-coded, `CENTRAL` is free.
- E3. `PUT` of the default warehouse with `isActive: false` **and** a code taken by another warehouse ->
  `DEFAULT_WAREHOUSE` (R4). With an invalid body -> `400`.
- E4. `DELETE` of an unused default warehouse -> `DEFAULT_WAREHOUSE`; of a default warehouse with posted
  documents -> `DEFAULT_WAREHOUSE` (not `IN_USE`).
- E5. Set-default of W2, then `PUT` W2 `isActive: false` -> `DEFAULT_WAREHOUSE`; the former default can now
  be deactivated.
- E6. Set-default twice in parallel on two different warehouses: both `200`; afterwards exactly one of them is
  the default.
- E7. A draft created without `warehouseId`; then the default is changed. The draft still names the old
  warehouse and posts into it (if it is active).
- E8. `"warehouseId": null` and no `warehouseId` are the same on create. `"warehouseId": ""` and `"abc"` are
  `400`, as before.
- E9. A transfer without `warehouseId`, with `toWarehouseId` = the default -> `400` with key `warehouseId`
  only.
- E10. A goods receipt with `purchaseOrderId` and without `warehouseId`, for an order into W2 while the
  default is `CENTRAL` -> created on W2.
- E11. Stock list of a warehouse in a tenant without stock articles -> `200`, `total == 0`.
- E12. An article created after stock was posted appears on every warehouse's stock list at once, with zeros.
- E13. An article with a confirmed purchase order into the warehouse and no stock: `quantity == 0`,
  `incomingQuantity > 0`; `hasStock=false` includes it.
- E14. A posted count without differences (008/R14) writes no entry and changes no balance.
- E15. Rebuild in a tenant with no postings -> `200` `{ "pairs": 0, "corrected": 0 }`.
- E16. A pair that went to zero: absent from stock on hand (as before), present with `quantity == 0` on the
  stock list, not a difference.

## 8. Tenant isolation

- T1. `StockBalance` is tenant-owned (filter, stamping, write check; tenant-inclusive keys). Each tenant has
  its own default warehouse; codes `CENTRAL` of different tenants are unrelated.
- T2. Another tenant's warehouse is non-existent for set-default and for the stock list (`404`), and for
  `warehouseId` on a document (`REFERENCE_NOT_FOUND`), as before.
- T3. An omitted `warehouseId` resolves to the **caller's** tenant's default, never to another tenant's.
- T4. The stock list contains only the caller's tenant's articles and quantities.
- T5. Verify and rebuild read and write the caller's tenant only: one tenant's rebuild never changes another
  tenant's balances; one tenant's differences are never listed to another.
- T6. The same through the tools. No `IgnoreQueryFilters()` is added. The migration is the only code that
  touches several tenants, and it does so tenant by tenant.

## 9. Security requirements

- S1. Tenant key on every new route and tool; admin key -> `403` (inherited). Tenant provisioning stays
  admin-only and without an MCP tool.
- S2. No operation lets a caller set a stock quantity. The stored balance is not an input anywhere: a
  property such as `quantity` or `isDefault` in a body that does not define it is rejected as unknown.
- S3. Rebuild is not a way to change stock: its result is determined by the ledger alone. It is still a
  write that takes the tenant's lock; nothing slow happens inside it beyond the database work
  (ADR-0012 amendment, condition 2).
- S4. `StockBalance.Quantity >= 0` and "one default per tenant, and active" are enforced by the database as a
  second barrier; neither is ever reported to a client as a database error (no `500`).
- S5. A difference found by verify is evidence of a defect: the server logs it (tenant id, pair ids,
  quantities) when verify or rebuild finds one. No other tenant's data appears in any response.

## 10. Acceptance criteria

Conventions as in specs 005 §10, 008 §10, 009 §10 and 010 §10: "the standard setup" (unit `pcs`, stock
articles `A` and `B`, service article `S`, warehouses `W1` and `W2`), "receive n of A into W", "Stock(A, W)",
`box` with A / box = 12, partners `SUP` and `CUS`, "PO […]", "SO […]".
- `DW` is the tenant's default warehouse: the single item of `GET /warehouses?isDefault=true`.
- "Ledger(A, W)" is the sum of `quantity` over all pages of `GET /stock-ledger-entries?articleId=A&warehouseId=W`.
- "Differences" is `total` of `GET /stock-balance-differences`.
- "StockList(W)" is all pages of `GET /warehouses/{W}/stock`; "Listed(A, W)" its item for article A.
- "**Balanced**" means, for every article and warehouse the test used: Stock(A, W) == Ledger(A, W) ==
  Listed(A, W).`quantity`, each `>= 0`; Differences is 0.
Unmarked criteria are black-box (tester). Quantities are compared numerically.

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  1. the literal tool list (57 names);
  2. tests asserting the exact property set of a Warehouse representation or of a warehouse tool's
     `outputSchema` (they gain `isDefault`), and the exact input properties of `warehouse_list`;
  3. tests that rely on a new tenant having **no** warehouse — an empty `GET /warehouses` / `warehouse_list`,
     its `total`, or the exact ordered list of all warehouse codes (004/AC-75, AC-82, AC-83, AC-130, AC-135
     and their like). They change by exactly the default warehouse (`total` one higher, `CENTRAL` in its
     place in the order, "empty" becomes "only the default"), or narrow their request with `search`;
     nothing else in them changes;
  4. tests expecting `400` with key `warehouseId` for a **create** of a receipt, issue, count, purchase order
     or sales order whose `warehouseId` is missing or `null`, over HTTP or a tool (005/AC-26 and its
     successors), and tests pinning `warehouseId` in `required` of `stock_document_create`,
     `purchase_order_create`, `sales_order_create`. The case moves to AC-30. Tests of `PUT`, of transfers and
     of a malformed `warehouseId` do not change;
  5. the builder's unit tests of tenant provisioning.
  No test that asserts a quantity, a number, a posting result or an order's progress changes. One migration
  added.
- AC-02 *(builder, model)* The model tests of 001/AC-06 and 002/AC-04 pass with `StockBalance`; the table test
  names it. The database refuses a second default warehouse for a tenant, an inactive default warehouse and a
  negative `StockBalance.Quantity`.
- AC-03 *(builder)* Reads use the stored balance, and verify and rebuild work: after a test changes one
  `StockBalance` row directly in the database (stored 12 where the ledger sums to 10) and deletes another,
  `GET /stock-on-hand` shows 12 for the first pair; `GET /stock-balance-differences` lists exactly those two
  pairs with `storedQuantity`, `ledgerQuantity`, `differenceQuantity` (12 / 10 / 2 and 0 / n / −n);
  `POST /stock-balances/rebuild` -> `corrected == 2`; then Differences is 0 and stock on hand shows the
  ledger sums. Another tenant's rows are untouched throughout.
- AC-04 *(builder)* Migration (R29–R31), run against a database at the previous migration with: a tenant with
  two active warehouses and posted stock; a tenant whose only warehouse is inactive and has the code
  `CENTRAL`; a tenant with no warehouse. Afterwards each has exactly one default warehouse, active — the
  older active one; a new `CENTRAL-2`; a new `CENTRAL` — and for every pair the stored balance equals the
  ledger sum.
- AC-05 *(manual)* `StockBalance` is written by one piece of code used by every posting and reversal, and by
  rebuild; a search of the solution finds no other writer. A posting failure after the entries were added
  cannot leave a balance behind (one transaction).
- AC-06 *(builder, unit)* The resolution of an omitted `warehouseId` (R8–R10) and the default-warehouse rules
  (R4–R6) are unit-tested without HTTP.

Inherited behaviour — smoke
- AC-10 `POST /warehouses/{uuid}/set-default`, `GET /warehouses/{uuid}/stock`,
  `GET /stock-balance-differences`, `POST /stock-balances/rebuild` without a credential -> `401`
  `UNAUTHENTICATED`; with the admin key -> `403` `FORBIDDEN`.
- AC-11 `GET /warehouses/{DW}/stock?foo=1` -> `400` with key `foo`; `?hasStock=yes` -> `400` with key
  `hasStock`; `?limit=0` -> `400` with key `limit`; `GET /stock-balance-differences?warehouseId=…` -> `400`
  with key `warehouseId`; `GET /warehouses?isDefault=1` -> `400` with key `isDefault`;
  `POST /warehouses` and `PUT /warehouses/{id}` with `"isDefault": true` in the body -> `400`.

The default warehouse
- AC-20 A tenant created with `POST /api/v1/admin/tenants` (response as 001/AC-11, unchanged): with its key,
  `GET /warehouses` -> `total == 1`; the item has `code == "CENTRAL"`, `name == "Central warehouse"`,
  `isDefault == true`, `isActive == true`, the six address properties `null`,
  `createdBy == updatedBy ==` the `apiKey.id` of the provisioning response. `GET /warehouses?isDefault=true`
  returns it; `?isDefault=false` -> `total == 0`; `GET /warehouses/by-code/central` returns it.
- AC-21 `POST /warehouses` `{ "code": "W1", "name": "Shop" }` -> `201` with `isDefault == false`;
  `GET /warehouses?isDefault=true` still returns only `CENTRAL`; `?isDefault=false` returns only `W1`.
  `POST /warehouses` with code `central` -> `409` `CODE_TAKEN`.
- AC-22 The default is an ordinary warehouse to `PUT`: new code, new name, an address, `isActive: true` ->
  `200`, `isDefault == true`; afterwards `POST /warehouses` with code `CENTRAL` -> `201`,
  `isDefault == false`.
- AC-23 `PUT` of `DW` with `isActive: false` (otherwise valid) -> `409` `DEFAULT_WAREHOUSE` with `errors` key
  `isActive`; `GET` shows it unchanged and active. The same body with another warehouse's code as well ->
  still `DEFAULT_WAREHOUSE`.
- AC-24 `DELETE` of `DW` -> `409` `DEFAULT_WAREHOUSE`, in a new tenant (nothing uses it) and after a receipt
  was posted into it; `DW` still exists and is the default.
- AC-25 `POST /warehouses/{W1}/set-default` -> `200`, body is W1 with `isDefault == true`, `updatedBy ==` the
  acting key. `GET /warehouses?isDefault=true` returns exactly W1; the former default has
  `isDefault == false` and `updatedBy ==` the acting key. Then: `PUT` of W1 with `isActive: false` and
  `DELETE` of W1 -> `DEFAULT_WAREHOUSE`; `PUT` of the former default with `isActive: false` -> `200`;
  `DELETE` of the (unused) former default -> `204`.
- AC-26 Set-default of the warehouse that already is the default -> `200`, the representation JSON-equal to
  the one before (`updatedAt` included). Set-default of an inactive warehouse -> `409` `DEFAULT_WAREHOUSE`
  with key `isActive`; the default is unchanged. Set-default of a random UUID and of `not-a-uuid` -> `404`
  `NOT_FOUND`.
- AC-27 Race, two targets: `set-default` of W1 and of W2 sent in parallel, at least ten rounds -> both `200`
  every round; after each round `GET /warehouses?isDefault=true` has `total == 1` and its item is W1 or W2.
- AC-28 Race, default against deactivation: `set-default` of W2 and `PUT` of W2 with `isActive: false` sent
  in parallel, at least ten rounds (W2 reactivated and `DW` restored between rounds) -> each round is either
  (`200`, `409 DEFAULT_WAREHOUSE`) with W2 the active default, or (`409 DEFAULT_WAREHOUSE`, `200`) with W2
  inactive and the default unchanged; no other outcome; always exactly one default, and it is active.

A warehouse on every document
- AC-30 In the standard setup, without `warehouseId` (omitted, and once with `null`): `POST /stock-documents`
  of a receipt, of an issue and of a count, `POST /purchase-orders` and `POST /sales-orders` -> each `201`
  with `warehouse.id == DW`. `GET` of each returns the same warehouse. Posting the receipt of 10 of A ->
  Stock(A, DW) is 10; the ledger entry has `warehouse.id == DW`.
- AC-31 The default at the moment of creation counts: a draft receipt created without `warehouseId`; then
  `set-default` of W1; the draft still has `warehouse.id ==` the former default and posts into it. A second
  receipt created without `warehouseId` now has `warehouse.id == W1`.
- AC-32 Naming a warehouse is unchanged: a receipt with `warehouseId: W2` is on W2; a random UUID -> `409`
  `REFERENCE_NOT_FOUND` with key `warehouseId`; an inactive warehouse -> `409` `REFERENCE_INACTIVE`; `"abc"`
  -> `400` with key `warehouseId`.
- AC-33 Not on replace, not on transfers: `PUT` of a draft receipt, of a draft purchase order and of a draft
  sales order without `warehouseId`, and with `"warehouseId": null` -> `400` with key `warehouseId`; the
  draft is unchanged. `POST /stock-documents` of a transfer without `warehouseId` and with
  `toWarehouseId: W2` -> `400` with key `warehouseId` and without `toWarehouseId`.
- AC-34 Linked documents take the order's warehouse: PO [(A, 10, 1)] into W2 (the default is another
  warehouse); `POST /stock-documents` `{ "type": "receipt", "documentDate": …, "purchaseOrderId": the order,
  "lines": [ { "articleId": A, "quantity": 4, "orderLineNo": 1 } ] }` -> `201`, `warehouse.id == W2`; it
  posts; Stock(A, W2) is 4. Mirrored: SO from W2 with stock there, an issue with `salesOrderId` and without
  `warehouseId` -> `201` on W2, posts. With a random UUID as `purchaseOrderId` and no `warehouseId` -> `409`
  `REFERENCE_NOT_FOUND` with key `purchaseOrderId` and without `warehouseId`.
- AC-35 An order created without `warehouseId` receives and delivers in the default warehouse: PO
  [(A, 10, 1)] without `warehouseId`, confirmed; `incomingQuantity` of (A, DW) is 10; a linked receipt
  without `warehouseId` posts into `DW`.

The stock list
- AC-40 In the standard setup with nothing posted: `GET /warehouses/{W1}/stock` -> `200`, `total == 2`; items
  for `A` then `B`, each with `article`, `warehouse.id == W1`, `unit.code == "pcs"` and `quantity`,
  `incomingQuantity`, `reservedQuantity`, `availableQuantity` all `0`; no item for `S`. The same for W2 and
  for `DW`. `GET /stock-on-hand` is empty.
- AC-41 Receive 100 of A into W1. StockList(W1): A has `quantity == 100`, `availableQuantity == 100`; B has
  `0`. StockList(W2): A and B both `0`. The item for A in StockList(W1) is JSON-equal to the item of
  `GET /stock-on-hand?articleId=A&warehouseId=W1`.
- AC-42 All four quantities: with 100 of A in W1, SO [(A, 30, 1)] from W1 and PO [(B, 60, 1)] into W1:
  Listed(A, W1) has `quantity == 100`, `reservedQuantity == 30`, `availableQuantity == 70`; Listed(B, W1) has
  `quantity == 0`, `incomingQuantity == 60`. Every item of StockList(W1) whose pair is in
  `GET /stock-on-hand?warehouseId=W1` is JSON-equal to it, and every stock-on-hand item of W1 is on the list.
- AC-43 Zero stays on the list: issue all 100 of A from W1 -> the pair is no longer in `GET /stock-on-hand`;
  Listed(A, W1) has `quantity == 0`.
- AC-44 All stock articles, always: a stock article `C` created after the postings is on StockList(W1) and
  StockList(W2) with zeros (`total == 3`); after `C` is deactivated it is still there; the service article is
  never there. A warehouse `W3` created now has `total == 3`, all zeros.
- AC-45 Filters and paging: with articles `A` (stock 100 in W1), `B` (0), `C` (inactive, 0):
  `hasStock=true` -> only A; `hasStock=false` -> B and C; `isActive=false` -> only C; `isActive=true` -> A and
  B; `search=` a substring of B's name in another letter case -> only B; `search=zzz` -> `total == 0`;
  `hasStock=false&isActive=true` -> only B; `limit=1&offset=1` -> the second item by article code with
  `total == 3`. Articles created as `b`, `A`, `c` are listed `A`, `b`, `c`.
- AC-46 Units: after a receipt of 2 `box` of A into W1, Listed(A, W1) has `quantity == 24` and
  `unit.code == "pcs"`.
- AC-47 `GET /warehouses/{random UUID}/stock` and `/warehouses/not-a-uuid/stock` -> `404` `NOT_FOUND`. The
  stock list of an inactive warehouse with stock -> `200` with that stock.
- AC-48 After article A is renamed and re-coded, the stock list shows its new code and name, in the new
  place in the order, with the same quantity.

The stored balance
- AC-50 **Balanced after every kind of posting.** In one tenant, in this order, checking Balanced after each
  step: a receipt of A and B into W1; an issue; a transfer W1 -> W2; a count with a surplus, a shortage and a
  line without difference; a receipt in boxes; the reversal of the transfer; the reversal of the count; a
  goods receipt against a purchase order; a delivery against a sales order; the reversal of the delivery; a
  receipt into `DW` created without `warehouseId`.
- AC-51 A refused posting changes no balance: with 10 of A in W1, an issue of 11 -> `409`
  `INSUFFICIENT_STOCK`; an outdated count -> `409` `COUNT_OUTDATED`; a delivery above the order -> `409`
  `QUANTITY_EXCEEDS_ORDER`; the reversal of a receipt whose goods were issued -> `409` `INSUFFICIENT_STOCK`.
  After each: Listed(A, W1).`quantity` is unchanged and Balanced holds.
- AC-52 Drafts, orders and masters change no balance: creating, replacing and deleting drafts, confirming,
  closing and reopening orders, renaming an article, `set-default` -> Listed quantities unchanged, Balanced.
- AC-53 Verify on a healthy tenant: `GET /stock-balance-differences` -> `200`
  `{ "items": [], "total": 0, "limit": 50, "offset": 0 }`, in a new tenant and after AC-50.
- AC-54 Rebuild on a healthy tenant: in a new tenant `POST /stock-balances/rebuild` -> `200`
  `{ "pairs": 0, "corrected": 0 }`. After AC-50 -> `corrected == 0` and `pairs ==` the number of
  (article, warehouse) pairs with at least one ledger entry (counted from `GET /stock-ledger-entries`);
  every Listed quantity, every stock-on-hand item, every document, the ledger (same entries, same `total`)
  and the next document numbers are as before; a second rebuild returns the same body.
- AC-55 **Race, issues.** 005/AC-46 (exactly 10 of A in W1, ten issues of 3 posted in parallel): after it
  Listed(A, W1).`quantity == 1` and Balanced.
- AC-56 **Race, mixed postings.** With 1000 of A and of B in W1: at least 30 drafts — receipts into W1 and
  W2, issues from W1, transfers W1 -> W2 and W2 -> W1, counts of B in W1, reversals of earlier receipts —
  posted in parallel; every response is `200` / `201` or a `409` with a code its type allows
  (`INSUFFICIENT_STOCK`, `COUNT_OUTDATED`, `INVALID_STATE`), none `500`. Afterwards Balanced; for every pair
  Listed.`quantity` equals the sum computed by the test from the ledger entries of the documents that
  succeeded.
- AC-57 **Race, verify during postings.** While 30 receipts and issues of A in W1 are posted in parallel,
  `GET /stock-balance-differences` is called repeatedly (at least 30 times) -> every call `200` with
  `total == 0`.
- AC-58 **Race, rebuild during postings.** While 30 receipts and issues of A in W1 are posted in parallel,
  `POST /stock-balances/rebuild` is called at least five times -> every call `200` with `corrected == 0`;
  every posting ends `200` or `409 INSUFFICIENT_STOCK`; afterwards Balanced and the numbers of the posted
  documents are gapless per type.
- AC-59 The check reads what the list shows: with Listed(A, W1).`quantity == 10`, an issue of 10 posts and an
  issue of 1 after it -> `409` `INSUFFICIENT_STOCK`; a count of A saved now has `bookQuantity == 0`.

MCP
- AC-70 `tools/list` returns exactly 57 names (literal list): the 53 of spec 010 and `warehouse_set_default`,
  `warehouse_stock_list`, `stock_balance_difference_list`, `stock_balance_rebuild`. Each new tool has a
  description, a closed `inputSchema` with described properties and `required` as in section 5, an
  `outputSchema` and its annotations. `warehouse_list` has `isDefault`; `warehouseId` is not in `required` of
  `stock_document_create`, `purchase_order_create`, `sales_order_create`.
- AC-71 Through tools only, in a new tenant with a unit and a stock article A: `warehouse_list`
  `{ "isDefault": true }` -> one item, `CENTRAL`; `stock_document_create` (receipt, A, 100, no `warehouseId`)
  -> tool success, `warehouse.code == "CENTRAL"`; `stock_document_post` -> tool success;
  `warehouse_stock_list` `{ "id": DW }` -> A with `quantity == 100`; `stock_balance_difference_list` `{}` ->
  `total == 0`; `stock_balance_rebuild` `{}` -> `{ "pairs": 1, "corrected": 0 }`; `warehouse_create` then
  `warehouse_set_default` -> tool success, `isDefault == true`, `updatedBy ==` the MCP key. Each result
  equals HTTP for the corresponding request.
- AC-72 `warehouse_stock_list` with `{ "id": W1, "hasStock": false, "isActive": true, "search": "…",
  "limit": 1, "offset": 1 }`; `purchase_order_create` and `sales_order_create` without `warehouseId` -> tool
  success, equal to HTTP.
- AC-73 Tool errors with the same `code` and `errors` keys as HTTP: `warehouse_update` of the default with
  `isActive: false` -> `DEFAULT_WAREHOUSE` (`isActive`); `warehouse_delete` of the default ->
  `DEFAULT_WAREHOUSE`; `warehouse_set_default` of an inactive warehouse -> `DEFAULT_WAREHOUSE` (`isActive`),
  of a random UUID -> `NOT_FOUND`; `warehouse_stock_list` with a random UUID -> `NOT_FOUND`, with
  `{ "id": W1, "foo": 1 }` -> `VALIDATION_FAILED`; `stock_document_update` without `warehouseId` ->
  `VALIDATION_FAILED` (`warehouseId`); `stock_document_create` of a transfer without `warehouseId` ->
  `VALIDATION_FAILED` (`warehouseId`); `stock_balance_rebuild` with `{ "tenantId": … }` ->
  `VALIDATION_FAILED`.

Tenant isolation (tenants X and Y, each with the standard setup)
- AC-80 Each tenant has its own default: both have a warehouse `CENTRAL` with `isDefault == true` and
  different ids. Y: `set-default`, `GET …/stock`, `PUT`, `DELETE` on X's default warehouse id -> `404`
  `NOT_FOUND`; through tools -> `NOT_FOUND`. X's default is unchanged.
- AC-81 Y's `set-default` of its own W1 leaves X's default unchanged; a receipt X then creates without
  `warehouseId` is on X's `CENTRAL`, one Y creates is on Y's W1.
- AC-82 X receives 100 of its `A` into its W1. Y's StockList of its own W1 shows Y's `A` with
  `quantity == 0` and only Y's articles; Y's `GET /stock-balance-differences` -> `total == 0`.
- AC-83 Y's `POST /stock-balances/rebuild` -> `{ "pairs": 0, "corrected": 0 }` although X has postings;
  afterwards X is Balanced with Stock(A, W1) still 100. X's rebuild returns `pairs == 1`.

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on three invariants: one active default per tenant whatever is done to warehouses
  (AC-23–AC-28); every document ends up on a warehouse (AC-30–AC-35); and **Balanced** after everything,
  including the races (AC-50–AC-58). Write `Balanced` once as a helper and call it at the end of the race
  tests of this spec; it must page through the ledger.
- A difference cannot be produced through the API — that is the point. AC-03 (a corrupted row) is the
  builder's. Black-box, verify is only ever seen empty; do not try to provoke a difference.
- The stock list item is deliberately the stock-on-hand item: compare whole items (AC-41, AC-42).
- Approved changes to earlier tests are exactly those of AC-01. If another earlier test fails because a new
  tenant now has a warehouse, or because a create without `warehouseId` now succeeds, and it does not fit
  items 3 or 4, write it in `docs/questions/011-q.md` before changing it.

Builder
- Provisioning: tenant, key and warehouse in the one transaction `ITenantProvisioningStore.AddAsync` already
  is. The warehouse's actor is the first key.
- Default warehouse: enforce R2 in the database (partial unique index, check constraint) and serialise
  set-default against warehouse replace and delete — under the per-tenant lock or by locking the warehouse
  rows; the unlocked check-then-write fails AC-28. The plain warehouse `PUT` of a non-default warehouse needs
  no new lock for anything else (open item 005/5 stands).
- Resolving an omitted `warehouseId` is Application code shared by stock documents and orders (AC-06); the
  endpoint and the tool pass the absence through. Keep "omitted" and `null` equal on create and distinguish
  both from a malformed value.
- `StockBalance`: one writer used by every code path that adds `StockLedgerEntry` rows (posting, reversal,
  all four types), inside the transaction that holds the tenant lock: upsert `Quantity = Quantity + entry`.
  Then switch the readers (R23) to it: stock on hand, the sufficiency checks, the count's book quantity and
  "current" check. The four quantities of the stock list come from the same query as stock on hand, started
  from the stock articles instead of from the pairs — one statement, `total` and paging in SQL.
- Verify must read ledger sums and balances as of one moment: one SQL statement, or a snapshot transaction.
  Two separate reads fail AC-57. It takes no lock.
- Rebuild: under the tenant lock, one transaction; compute the sums, count the pairs that differ, write the
  rows. Set-based SQL, not a loop over entries in memory. Log every corrected pair (S5).
- The migration does the same per tenant with plain SQL (R29–R31); test it as AC-04 describes.
- Anything unclear or contradictory: `docs/questions/011-q.md`, then continue with the rest.

## 12. Architect's defaults for the owner

Each stands until the owner says otherwise; the rule that carries it is named.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | Code and name of the warehouse a new tenant gets | `CENTRAL`, "Central warehouse" | R1 |
| 2 | Can it be renamed and re-coded? | Yes, like any warehouse; only `isDefault` marks it | R3 |
| 3 | Can the default warehouse be deactivated or deleted? | No (`DEFAULT_WAREHOUSE`); another warehouse must be made the default first | R4, R5 |
| 4 | Can a tenant change which warehouse is the default? | Yes, with `set-default`; the target must be active | R6 |
| 5 | Tenants that already exist | Their oldest active warehouse becomes the default; a new `CENTRAL` only if they have none | R29 |
| 6 | Must the caller name the warehouse on a document? | No on create — omitted means the default (on a linked receipt or delivery: the order's warehouse). Yes on replace and for both warehouses of a transfer | R8–R10 |
| 7 | Future document types (invoices) | Every one carries a warehouse, with the same omission rule | R12 |
| 8 | What the stock list shows | Every stock article, active or not, zero included, with the same four quantities as stock on hand; no service articles | R13–R15 |
| 9 | Who reads the stored balance | Everything: stock on hand, the stock list, the no-negative-stock check, the count | R23 |
| 10 | Who may verify and rebuild | Any tenant key, until permissions exist | R27 |
| 11 | What happens when verify finds a difference | It is reported and logged; nothing is corrected automatically — rebuild is a separate, explicit call | R24, R25 |
