# Spec 009 — Purchase orders and goods receipt

Status: ready for the tester and the builder once spec 008 is merged to `main`.
Branches: `tests/009-purchase-orders-goods-receipt` (tester), `feat/009-purchase-orders-goods-receipt` (builder).
Read first: specs 004 (partners), **005**, **006**, **007** (this spec extends them and repeats none of them),
ADR-0008, ADR-0012, ADR-0013, ADR-0014, **ADR-0016**. Why this is the ninth spec: `docs/roadmap.md`
section 3 — inbound before outbound.
Spec 010 mirrors this spec for sales and reuses its error codes and the line field `orderLineNo`.

**Inherited, not re-specified** (roadmap §4): everything specs 001–008 established — transport, errors,
references, MCP mapping, and all rules for stock documents, lines and units, posting, reversal, numbering,
stock on hand and the ledger. This spec states only what is new or changed. Numbers are local; "005/R15" means
rule R15 of spec 005.

## 1. Goal

Let a tenant order goods from a supplier and receive them, in one or several deliveries, with the system
knowing at every moment how much of each order line has arrived.

- A **purchase order** names a supplier, a warehouse and lines with article, unit, quantity and unit price.
  It is prepared as a draft and then **confirmed**: it gets its number and can no longer be edited.
- A **goods receipt** is a stock document of type `receipt` (spec 005) **linked to the order**. Posting it
  writes the stock ledger exactly as any receipt does and raises the received quantity of the order lines.
- An order line is never received beyond what was ordered. A reversal of a receipt gives the quantity back.
- A confirmed order can be **closed** (nothing more will come) and reopened.

## 2. Scope

In scope
1. `PurchaseOrder` with lines; operations list, get, get by number, create, replace, delete, **confirm**,
   **close**, **reopen** — each as an HTTP endpoint and an MCP tool.
2. The link from a stock document of type `receipt` to a purchase order and its lines; the checks it adds to
   saving, posting and reversal.
3. `incomingQuantity` in stock on hand.
4. Changes to existing operations: further `IN_USE` cases for partner, warehouse, article, unit of measure and
   article unit.

Out of scope
- Editing a confirmed order, order versions, a `cancelled` status, approval steps (ADR-0016).
- Over-receipt and tolerances; receipt into another warehouse than the order's; automatic closing.
- `service` articles on orders; taxes, discounts, currencies, price lists, payment terms (014, later).
- Cost and value of what was received (011); purchase invoices (014); returns to the supplier (later;
  a whole receipt is undone by reversal, 006).
- Supplier-specific article codes, minimum order quantities, purchase requisitions, reorder proposals.

## 3. Data

`PurchaseOrder` (tenant-owned): `Id` uuid v7, `TenantId`, `Status` (`draft` | `confirmed` | `closed`),
`Number` (null until confirmed), `OrderDate` (date), `ExpectedDate` (date, nullable), `SupplierId`,
`WarehouseId`, `Reference` (nullable, max 100), `Note` (nullable, max 2000), `ConfirmedAt`, `ConfirmedBy`,
`ClosedAt`, `ClosedBy` (nullable), audit columns.

`PurchaseOrderLine` (tenant-owned): `Id`, `TenantId`, `OrderId`, `LineNo` (1…n), `ArticleId`, `UnitId`,
`Quantity` numeric(18,6), `UnitPrice` numeric(18,6), `Factor` and `BaseQuantity` (set at confirmation).

`StockDocument` gains `PurchaseOrderId` (nullable); `StockDocumentLine` gains `OrderLineNo` (nullable).
`DocumentCounter` gets the type `purchaseOrder`.

Keys: every new foreign key (to `Partner`, `Warehouse`, `Article`, `UnitOfMeasure`, `ApiKey`, from a stock
document to its order and from a linked stock document line to its order line) includes `TenantId` and
restricts deletes. Unique `(TenantId, Number)` on orders (where not null); unique `(TenantId, OrderId, LineNo)`
on lines. One migration.

## 4. Operations — HTTP

All routes are under `/api/v1` and require a tenant API key.

### 4.1 Purchase orders

Representation (`PurchaseOrder`):
```json
{ "id": "uuid", "status": "confirmed", "number": "PO-000001",
  "orderDate": "2026-10-09", "expectedDate": null,
  "supplier": { "id": "uuid", "code": "SUP", "name": "Bolt Works Ltd" },
  "warehouse": { "id": "uuid", "code": "WH-1", "name": "Main warehouse" },
  "reference": null, "note": null,
  "receiptStatus": "partial",
  "lines": [
    { "lineNo": 1,
      "article": { "id": "uuid", "code": "ART-001", "name": "Steel bolt M8" },
      "unit": { "id": "uuid", "code": "box", "name": "Box" }, "quantity": 5,
      "unitPrice": 30, "lineAmount": 150,
      "factor": 12, "baseUnit": { "id": "uuid", "code": "pcs", "name": "Piece" }, "baseQuantity": 60,
      "receivedBaseQuantity": 24, "outstandingBaseQuantity": 36 } ],
  "totalAmount": 150,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid",
  "confirmedAt": "…Z", "confirmedBy": "uuid", "closedAt": null, "closedBy": null }
```
All properties are always present. In a list, each item is the same object **without** `lines` and with
`"lineCount": int`.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /purchase-orders?status=&receiptStatus=&supplierId=&warehouseId=&search=&limit=&offset=` | `200` list envelope of order summaries | 400 |
| Get | `GET /purchase-orders/{id}` | `200` PurchaseOrder | 404 |
| Get by number | `GET /purchase-orders/by-number/{number}` | `200` PurchaseOrder | 404 |
| Create | `POST /purchase-orders` body `{ "orderDate", "supplierId", "warehouseId", "lines": [ { "articleId", "quantity", "unitPrice", "unitId"? } ], "expectedDate"?, "reference"?, "note"? }` | `201` PurchaseOrder (`draft`), `Location` | 400, 409 `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `PARTNER_ROLE_MISSING`, `ARTICLE_NOT_STOCKED`, `UNIT_NOT_ON_ARTICLE`, `QUANTITY_NOT_CONVERTIBLE` |
| Replace | `PUT /purchase-orders/{id}` body `{ "orderDate", "expectedDate", "supplierId", "warehouseId", "reference", "note", "lines" }` (all seven present) | `200` PurchaseOrder | 400, 404, 409 `INVALID_STATE` and as Create |
| Delete | `DELETE /purchase-orders/{id}` | `204` | 404, 409 `INVALID_STATE` |
| **Confirm** | `POST /purchase-orders/{id}/confirm` (no body) | `200` PurchaseOrder (`confirmed`, with `number`, `confirmedAt`, `confirmedBy`) | 404, 409 `INVALID_STATE`, `REFERENCE_INACTIVE`, `PARTNER_ROLE_MISSING`, `QUANTITY_NOT_CONVERTIBLE` |
| **Close** | `POST /purchase-orders/{id}/close` (no body) | `200` PurchaseOrder (`closed`) | 404, 409 `INVALID_STATE` |
| **Reopen** | `POST /purchase-orders/{id}/reopen` (no body) | `200` PurchaseOrder (`confirmed`) | 404, 409 `INVALID_STATE` |

### 4.2 Changes to the stock document

| Operation | Change |
|---|---|
| Create | Body gains `"purchaseOrderId"?` (uuid or `null`); a line gains `"orderLineNo"?` (integer or `null`). |
| Replace | A line gains `"orderLineNo"?`. `purchaseOrderId` is **not** part of the replace body. |
| List | New filter `purchaseOrderId=`. |
| Post | Additional errors: `409 ORDER_NOT_OPEN`, `409 QUANTITY_EXCEEDS_ORDER`. |
| Reverse | Works for a linked receipt (R32–R34). |

Representation: every stock document and list summary gains `"purchaseOrder"`: `{ "id", "number" }` of the
order on a linked document, otherwise `null`. Every line gains `"orderLineNo"`: the order line's number on a
linked document, otherwise `null`.

### 4.3 Stock on hand

Every item gains `"incomingQuantity"`: the quantity, in base units, still expected into that warehouse from
confirmed purchase orders (R35). The list now has one item per (article, warehouse) pair whose `quantity`
**or** `incomingQuantity` is not zero.

### 4.4 Changes to masters

- `DELETE /partners/{id}`, `/warehouses/{id}`, `/articles/{id}`, `/units-of-measure/{id}` -> `409 IN_USE` also
  while a purchase order (any status) names the record (R37).
- `PUT /articles/{id}` changing `type` or `baseUnitId` -> `409 IN_USE` also while a purchase order line names
  the article (005/R26).
- `DELETE /articles/{articleId}/units/{unitId}` -> `409 IN_USE` also while a line of a **draft** purchase order
  uses the conversion (007/R8).

### 4.5 New error codes (registry: architecture §6)

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 409 | `PARTNER_ROLE_MISSING` | The partner named on an order does not have the role the order needs (`isSupplier` for a purchase order). | `supplierId` |
| 409 | `ORDER_NOT_OPEN` | A stock document is saved or posted against an order whose status is not `confirmed`. | `purchaseOrderId` |
| 409 | `ORDER_MISMATCH` | A stock document linked to an order disagrees with it: another warehouse, or a line whose article is not the order line's article. | `warehouseId`, `lines[i].articleId` |
| 409 | `QUANTITY_EXCEEDS_ORDER` | Posting would take an order line above its ordered quantity. | `lines[i].quantity` for every line linked to such an order line |
| 409 | `INVALID_STATE` *(extended)* | Also: replace, delete or confirm of an order that is not a `draft`; close of an order that is not `confirmed`; reopen of an order that is not `closed`. | — |

## 5. Operations — MCP

| Tool | Arguments | Success | HTTP operation | Error codes |
|---|---|---|---|---|
| `purchase_order_list` | `{ status?, receiptStatus?, supplierId?, warehouseId?, search?, limit?, offset? }` | list envelope | List | `VALIDATION_FAILED` |
| `purchase_order_get` | `{ id?, number? }` — exactly one | PurchaseOrder | Get / Get by number | `VALIDATION_FAILED`, `NOT_FOUND` |
| `purchase_order_create` | `{ orderDate, supplierId, warehouseId, lines, expectedDate?, reference?, note? }` | PurchaseOrder | Create | as HTTP |
| `purchase_order_update` | `{ id, orderDate, expectedDate, supplierId, warehouseId, reference, note, lines }` | PurchaseOrder | Replace | as HTTP |
| `purchase_order_delete` | `{ id }` | `{ "deleted": true }` | Delete | `NOT_FOUND`, `INVALID_STATE` |
| `purchase_order_confirm` | `{ id }` | PurchaseOrder | Confirm | as HTTP |
| `purchase_order_close` | `{ id }` | PurchaseOrder | Close | `NOT_FOUND`, `INVALID_STATE` |
| `purchase_order_reopen` | `{ id }` | PurchaseOrder | Reopen | `NOT_FOUND`, `INVALID_STATE` |

- `lines` is an array of `{ articleId: uuid, quantity: number, unitPrice: number, unitId?: uuid | null }`
  (closed schema).
- Annotations: `_list`, `_get` read-only; `_create`, `_update`, `_delete` by the pattern of 003 §5.3.
  `purchase_order_confirm`: `readOnlyHint: false`, `destructiveHint: true`, `idempotentHint: false`,
  `openWorldHint: false`. `purchase_order_close` and `_reopen`: `readOnlyHint: false`,
  `destructiveHint: false`, `idempotentHint: false`, `openWorldHint: false`.
- Descriptions say:
  - `purchase_order_confirm` — confirmation is permanent: the order gets its number and can no longer be
    edited or deleted; to change a confirmed order, close it and create a new one.
  - `purchase_order_create` — `unitPrice` is per unit of the line (per box when the line is in boxes), in the
    tenant's currency, without tax; only stock articles; the supplier must be a partner with `isSupplier`.
  - `purchase_order_get` — how to receive: create a stock document of type `receipt` with `purchaseOrderId`,
    the order's warehouse, and one line per delivery position with `articleId`, `quantity` and `orderLineNo`,
    then post it; `outstandingBaseQuantity` is what can still be received, in base units.
  - `purchase_order_close` — use it when the rest will not be delivered, or to cancel an order nothing was
    received against; draft receipts against a closed order cannot be posted.
- Changed tools: `stock_document_create` gains `purchaseOrderId?` (uuid or null) and its `lines` item gains
  `orderLineNo?` (integer or null); the `lines` item of `stock_document_update` gains `orderLineNo?`;
  `stock_document_list` gains `purchaseOrderId?`. The description of `stock_document_post` names
  `ORDER_NOT_OPEN` and `QUANTITY_EXCEEDS_ORDER` and what to do (read the order: `outstandingBaseQuantity`).
  `partner_delete`, `warehouse_delete`, `article_delete`, `uom_delete`, `article_unit_delete`: descriptions
  mention the new `IN_USE` cases (review 004, non-blocking item 4).
- `tools/list` returns exactly 45 tools: the 37 of spec 007 and these 8.

## 6. Business rules

The order
- R1. A purchase order is addressed to one supplier (`supplierId`) and expects its goods in one warehouse
  (`warehouseId`). Both are required and follow the reference rules (ADR-0008) with their own `errors` keys.
- R2. The supplier must be a partner with `isSupplier == true`; otherwise `409 PARTNER_ROLE_MISSING` with key
  `supplierId`. The role is checked on every create and replace and at confirmation. It is not frozen: a
  partner on orders may later lose the role (004/R15 still requires one role); existing orders are unaffected.
- R3. `orderDate` is required, a real calendar date (005/R2). `expectedDate` is optional (`null` when absent);
  when given it is a real calendar date not earlier than `orderDate`, otherwise `400` with key `expectedDate`.
- R4. `reference` (the supplier's own number for the order) and `note` follow 005/R7. On replace all seven
  body fields must be present; `expectedDate`, `reference` and `note` may be `null`.
- R5. `lines`: 1 to 200. Each line has `articleId`, `quantity`, `unitPrice` and optionally `unitId`; lines are
  numbered 1…n in the order given and renumbered by a replace. The same article may be on several lines.
- R6. Line references follow 005/R5 and 007/R12 unchanged: unknown article or unit -> `REFERENCE_NOT_FOUND`;
  inactive and newly assigned -> `REFERENCE_INACTIVE`; a `service` article -> `ARTICLE_NOT_STOCKED`; a unit
  that is not a unit of the article -> `UNIT_NOT_ON_ARTICLE`. Keys `lines[i].articleId` / `lines[i].unitId`.
- R7. `quantity` follows 005/R6 (greater than 0, in the line's unit) and converts by 007/R14–R15
  (`QUANTITY_NOT_CONVERTIBLE`).
- R8. `unitPrice` is required: a JSON number, **0 or greater**, with at most 6 decimal places, at most
  999999999.999999; violations -> `400` with key `lines[i].unitPrice`. It is the price of one unit of the line
  (`unit`), in the tenant's currency, without tax. `0` is allowed (goods free of charge).
- R9. **Amounts.** `lineAmount` = `quantity` × `unitPrice`, rounded to 2 decimal places, half away from zero.
  `totalAmount` = the sum of the line amounts. A line whose `lineAmount` would exceed 9999999999.99 -> `400`
  with keys `lines[i].quantity` and `lines[i].unitPrice`. Amounts depend on nothing but the line's own
  `quantity` and `unitPrice`: they never change with a factor or with receipts.
- R10. Order of checks on create and replace: validation (`400`, all fields and lines together) -> order exists
  (`404`) -> order is a draft (`INVALID_STATE`) -> supplier (`REFERENCE_NOT_FOUND`, then `REFERENCE_INACTIVE`,
  then `PARTNER_ROLE_MISSING`) -> warehouse -> lines, by the kinds and in the order of 007/R16. Each stage
  answers alone.

Lifecycle
- R11. A new order is a `draft`: `number`, `confirmedAt`, `confirmedBy`, `closedAt`, `closedBy` are `null`.
  A draft orders nothing: it has no incoming quantity, cannot be received against, and can be replaced and
  deleted any number of times. `factor` and `baseQuantity` of a draft line follow the article's current
  conversion (007/R17).
- R12. **Confirm** (`draft -> confirmed`) is atomic. Order of checks: order exists -> is a draft
  (`INVALID_STATE`) -> supplier, warehouse and every article are active (`REFERENCE_INACTIVE`, keys
  `supplierId`, `warehouseId`, `lines[i].articleId`; header keys together first, then lines: 005/R13) -> the
  supplier still has the role (`PARTNER_ROLE_MISSING`) -> conversion with the current factors (`QUANTITY_NOT_CONVERTIBLE`). On success:
  `number` assigned, `confirmedAt` = now, `confirmedBy` = the acting key, and `factor` and `baseQuantity` of
  every line stored for good.
- R13. **Numbering.** `PO-` followed by the counter value padded to at least 6 digits; one counter per tenant,
  by the rules of 005/R17: gapless, in confirmation order; failed confirmations, drafts and deleted drafts
  consume nothing.
- R14. **A confirmed order is immutable.** Replace, delete and a second confirm -> `409 INVALID_STATE`. Its
  lines keep `unit`, `quantity`, `unitPrice`, `factor` and `baseQuantity` whatever happens to conversions.
  The only changes the application permits are the transitions of R15 and R16.
- R15. **Close** (`confirmed -> closed`): sets `closedAt` and `closedBy`. Allowed whatever was received — none,
  part or all. Close of a draft or of a closed order -> `INVALID_STATE`.
- R16. **Reopen** (`closed -> confirmed`): clears `closedAt` and `closedBy`. Reopen of a draft or of a
  confirmed order -> `INVALID_STATE`. Number, lines and received quantities are unchanged by close and reopen.
- R17. `updatedAt` / `updatedBy` change on replace only. Confirm, close and reopen are attributed by
  `confirmedBy` / `closedBy`; who reopened is the audit log's (016).

Goods receipt — the link
- R18. A stock document may carry `purchaseOrderId` only when its `type` is `receipt`; on any other type a
  non-null value -> `400` with key `purchaseOrderId`. Omitted or `null` means an unlinked document, which
  behaves exactly as before this spec.
- R19. The link is set at creation and never changes: `purchaseOrderId` in a replace body is an unknown
  property (`400`).
- R20. On a linked document every line must have `orderLineNo`: a JSON integer of 1 or more; missing, `null`,
  `0`, `1.5` or `"1"` -> `400` with key `lines[i].orderLineNo`. On an unlinked document `orderLineNo` must be
  omitted or `null`, otherwise `400` with the same key.
- R21. Saving a linked document (create and replace) checks, after the warehouse reference (005/R8):
  1. the order exists in the tenant — else `409 REFERENCE_NOT_FOUND`, key `purchaseOrderId`;
  2. its status is `confirmed` — else `409 ORDER_NOT_OPEN`, key `purchaseOrderId`;
  3. the line kinds of 007/R16, where an `orderLineNo` that is not a line of the order is a
     `REFERENCE_NOT_FOUND` with key `lines[i].orderLineNo`;
  4. agreement with the order — else `409 ORDER_MISMATCH` with every applicable key: `warehouseId` when the
     document's warehouse is not the order's; `lines[i].articleId` when a line's article is not the article
     of the order line it names.
- R22. A receipt line may use any unit of its article (007/R12), whatever the order line's unit. Several
  receipt lines may name the same order line. A receipt need not cover every order line.
- R23. Saving does **not** compare quantities with the order: a draft receipt reserves nothing of the order
  and may exceed it; the comparison is made at posting (R26).

Goods receipt — posting
- R24. Posting a linked receipt is the posting of specs 005–007 with two more checks. Order of checks:
  document exists -> is a draft -> warehouse and articles active -> conversion -> **the order is `confirmed`**
  (`409 ORDER_NOT_OPEN`, key `purchaseOrderId`) -> **the order's quantities** (R26). The supplier is not
  re-checked: goods on the way are received even from a partner deactivated since.
- R25. **Received quantity.** `receivedBaseQuantity` of an order line is the sum of `baseQuantity` over the
  stock document lines that name it on documents with status `posted` that are not reversing documents.
  Nothing else changes it. It is in base units, whatever units the order line and the receipt lines use.
- R26. **Never more than ordered.** Posting is refused with `409 QUANTITY_EXCEEDS_ORDER` if, for any order
  line, the document's `baseQuantity` for that line (summed over its lines naming it) exceeds the order line's
  `outstandingBaseQuantity` at that moment. `errors` keys are `lines[i].quantity` for every document line
  naming such an order line. Nothing is posted, not even the lines within the order; no number is consumed.
- R27. `outstandingBaseQuantity` of an order line is `baseQuantity − receivedBaseQuantity` while the order is
  `confirmed`, and `0` while it is a `draft` or `closed`. Therefore always
  `0 ≤ receivedBaseQuantity ≤ baseQuantity`.
- R28. A posted linked receipt writes exactly the ledger entries of 005/R14 and 007/R19 and gets an `SR-`
  number from the receipt counter: for stock it is a receipt like any other.
- R29. `receiptStatus` of an order: `"none"` when no line has a received quantity above 0; `"full"` when every
  line has `receivedBaseQuantity == baseQuantity`; otherwise `"partial"`. A draft is `"none"`. A fully
  received order stays `confirmed` until it is closed.
- R30. Concurrency: R26 and R27 hold when several receipts against one order are posted in parallel — only as
  many succeed as the order covers. A posting racing with a close either precedes it or gets
  `ORDER_NOT_OPEN`.
- R31. Against a `closed` (or draft) order: a linked document cannot be created or replaced
  (`ORDER_NOT_OPEN`) and a draft receipt made earlier cannot be posted; it can be read and deleted. After a
  reopen it posts by the rules above.

Goods receipt — reversal
- R32. A posted linked receipt is reversed by the rules of spec 006. The reversing document carries the same
  `purchaseOrder` and the same `orderLineNo` on every line.
- R33. After the reversal the original no longer counts (R25): the received quantity of its order lines drops
  by its base quantities, and the outstanding quantity of a `confirmed` order rises by as much.
- R34. A reversal does not depend on the order's status — a receipt against a closed order can be reversed —
  and is refused only for stock reasons (006/R16). It does not change the order's status.

Incoming quantity and reading
- R35. `incomingQuantity` of an (article, warehouse) pair = the sum of `outstandingBaseQuantity` over the lines
  with that article on `confirmed` purchase orders for that warehouse. Drafts and closed orders contribute
  nothing. Stock on hand lists a pair when `quantity` or `incomingQuantity` is not zero; `quantity` is, as
  before, the sum of the ledger (005/R19) — an order never changes it.
- R36. Order list: `status` and `receiptStatus` filter by exact value; `supplierId` and `warehouseId` by
  reference (002/R19); `search` matches case-insensitively as a substring of `number` or `reference`. Ordered
  by `createdAt` descending, then `id` descending. `by-number` lookup is case-insensitive. Summaries show the
  masters' current code and name (005/R23).

Effects on masters
- R37. A partner, a warehouse, an article and a unit of measure are **used** while a purchase order or one of
  its lines — draft, confirmed or closed — names them: `DELETE` -> `409 IN_USE`. They can be renamed, re-coded
  and deactivated. Deleting the last draft that names a record makes it unused again (005/R27).
- R38. A purchase order line freezes its article's `type` and `baseUnitId` exactly as a stock document line
  does (005/R26). A conversion used by a line of a draft order cannot be deleted (007/R8); lines of confirmed
  and closed orders carry their own factor and do not block it.

## 7. Edge cases

- E1. `unitPrice` missing, `null`, `-0.01`, `"2"`, `1.0000001`, `1000000000` -> `400` with
  `lines[i].unitPrice`; `0`, `0.000001`, `999999999.999999` -> accepted (subject to R9).
- E2. `lineAmount` rounding: 2.5 × 0.333333 -> `0.83`; 0.5 × 0.01 -> `0.01`; 0.5 × 0.03 -> `0.02`;
  1 × 0.004 -> `0`.
- E3. `expectedDate` one day before `orderDate` -> `400`; equal -> accepted; `"2026-02-30"` -> `400`.
- E4. A line with an extra property (`"lineNo"`, `"lineAmount"`, `"discount"`) -> `400`; a body with
  `"status"`, `"number"`, `"totalAmount"` or `"tenantId"` -> `400`.
- E5. Unknown supplier **and** unknown warehouse -> `REFERENCE_NOT_FOUND` with key `supplierId` only (R10).
- E6. A supplier that is also a customer (`isSupplier` and `isCustomer`) -> accepted.
- E7. A draft order whose supplier lost the supplier role: `GET` works; `PUT` keeping it and `confirm` ->
  `PARTNER_ROLE_MISSING`.
- E8. A factor changed between saving and confirming a draft changes `baseQuantity`, not `lineAmount`; after
  confirmation a factor change alters nothing on the order.
- E9. An order line in boxes received in pieces, and the reverse: only base quantities are compared.
- E10. A receipt of exactly the outstanding quantity -> posted; one base unit more (0.000001) -> refused.
- E11. A receipt with two lines for one order line, each within the outstanding quantity but together above
  it -> `QUANTITY_EXCEEDS_ORDER` with both keys.
- E12. A receipt with one line within and one line above its order line -> only the second line's key;
  nothing is posted.
- E13. Two draft receipts each for the full outstanding quantity: the first posts, the second gets
  `QUANTITY_EXCEEDS_ORDER` and stays a draft that can be corrected or deleted.
- E14. A fully received order: any further linked receipt -> `QUANTITY_EXCEEDS_ORDER` at posting; after a
  receipt is reversed, the quantity can be received again.
- E15. Close with draft receipts open -> allowed; they cannot be posted until a reopen.
- E16. `POST /stock-documents` with `purchaseOrderId` of a draft order -> `ORDER_NOT_OPEN`; with the id of a
  stock document or a random UUID -> `REFERENCE_NOT_FOUND`.
- E17. Confirming the same draft twice in parallel: one `200`, one `409 INVALID_STATE`; one number.
- E18. `confirm`, `close`, `reopen` with a query parameter (`?x=1`) -> `400` with key `x`.

## 8. Tenant isolation

- T1. `PurchaseOrder` and `PurchaseOrderLine` are tenant-owned; all new foreign keys include `TenantId`. The
  order counter is per tenant.
- T2. Another tenant's order is non-existent: not listed, not counted, `404 NOT_FOUND` on get, get-by-number,
  replace, delete, confirm, close and reopen.
- T3. Another tenant's partner, warehouse, article or unit as a reference on an order ->
  `REFERENCE_NOT_FOUND`; another tenant's order as `purchaseOrderId` on a stock document ->
  `REFERENCE_NOT_FOUND` with key `purchaseOrderId`; as a filter -> empty result.
- T4. Received and incoming quantities are computed from the caller's tenant's documents only. One tenant's
  orders never make another tenant's masters used.
- T5. The same through tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Any tenant key, `human` or `agent`, may create, confirm, close and reopen orders and post receipts
  against them (as 005/S2; restrictable from spec 017). `confirmedBy`, `closedBy` and the receipt's `postedBy`
  attribute each step to one key.
- S2. A confirmed order commits the tenant towards a third party but moves no stock and no money; the ledger
  is still written only by posting a stock document (005/S3 unchanged).
- S3. Through the DbContext a confirmed or closed order accepts no modification other than the transitions of
  R15 and R16.
- S4. Prices and amounts are tenant data like any other; they are not written to logs.

## 10. Acceptance criteria

Conventions, "the standard setup", "Stock(A, W1)" as in spec 005 §10; unit `box` with A / box = 12 as in spec
007 §10. In addition this spec's setup has partners `SUP` (`isSupplier` only) and `CUS` (`isCustomer` only).
- "PO [lines]" means: create a purchase order for `SUP` and `W1`, dated `2026-10-09`, with those lines
  (article, quantity, unit when not base, price), and confirm it.
- "Receive (k, n) against the order" means: create a receipt for `W1` with `purchaseOrderId` and one line
  `{ articleId: the article of order line k, quantity: n, orderLineNo: k }`, and post it.
- "Received(k)" and "Outstanding(k)" are `receivedBaseQuantity` and `outstandingBaseQuantity` of order line k
  in `GET /purchase-orders/{id}`; "Incoming(A, W1)" is `incomingQuantity` of that pair in
  `GET /stock-on-hand`, or 0 when the pair is absent.
Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The only earlier tests changed are:
  the literal tool list; tests asserting the exact property set of a stock document, of its list summary, of a
  stock document line or of a stock-on-hand item (they gain `purchaseOrder`, `orderLineNo`,
  `incomingQuantity`); tests pinning the exact input-schema properties of `stock_document_create`,
  `stock_document_list` or of a `lines` item. No test of spec 004 changes: an unused partner is still deleted
  with `204`. One migration added.
- AC-02 *(builder)* Through the DbContext, a confirmed or closed order and its lines accept no modification
  other than `confirmed <-> closed` with `closedAt` / `closedBy` (S3).
- AC-03 *(builder, unit)* The amount rule (R9, with the half-way cases of E2 and the maximum), the
  received / outstanding rule (R25–R27) and `receiptStatus` (R29) are unit-tested without HTTP.

Inherited behaviour — smoke
- AC-10 `GET /purchase-orders`, `POST /purchase-orders`, `POST /purchase-orders/{uuid}/confirm`, `/close`,
  `/reopen` without a credential -> `401`; with the admin key -> `403`.
- AC-11 `POST /purchase-orders` with an unknown property (`"status": "confirmed"`, `"number": "X"`,
  `"totalAmount": 1`) -> `400`; `GET /purchase-orders?foo=1` and `?status=open` -> `400` with key `foo` /
  `status`; `POST /purchase-orders/{id}/confirm?x=1` -> `400` with key `x`.

Drafts
- AC-20 `POST /purchase-orders` `{ "orderDate": "2026-10-09", "supplierId": SUP, "warehouseId": W1,
  "lines": [ { "articleId": A, "quantity": 10, "unitPrice": 2.5 } ] }` -> `201`; `status == "draft"`;
  `number`, `expectedDate`, `reference`, `note`, `confirmedAt`, `confirmedBy`, `closedAt`, `closedBy` all
  `null` (present); `supplier.id == SUP` and `warehouse.id == W1` with their codes and names;
  `receiptStatus == "none"`; one line with `lineNo == 1`, `article.id == A`, `unit.code == "pcs"`,
  `quantity == 10`, `unitPrice == 2.5`, `lineAmount == 25`, `factor == 1`, `baseUnit.code == "pcs"`,
  `baseQuantity == 10`, `receivedBaseQuantity == 0`, `outstandingBaseQuantity == 0`; `totalAmount == 25`;
  `createdBy ==` the acting key; `Location` ends with `/api/v1/purchase-orders/{id}`; `GET /{id}` returns the
  same.
- AC-21 A draft orders nothing: after AC-20, `GET /stock-on-hand` is empty and Incoming(A, W1) is 0; a receipt
  with `purchaseOrderId` = the draft -> `409` `ORDER_NOT_OPEN` with key `purchaseOrderId`, nothing created.
- AC-22 Lines, units and amounts: an order with lines (A, 5, box, 30), (B, 2.5, 0.333333), (A, 1, 0),
  (B, 0.5, 0.01) -> `201`; lines numbered 1–4; line 1 has `unit.code == "box"`, `factor == 12`,
  `baseQuantity == 60`, `lineAmount == 150`; the other `lineAmount`s are `0.83`, `0`, `0.01`;
  `totalAmount == 150.84`.
- AC-23 `PUT /{id}` on a draft with another `orderDate`, an `expectedDate`, another supplier (a second
  supplier partner), `W2`, a reference, a note and lines [(B, 7, 1.1)] -> `200` with the new values, exactly
  one line and `totalAmount == 7.7`; `id` unchanged; `status == "draft"`; `updatedBy ==` the acting key.
- AC-24 `PUT` omitting `orderDate`, `expectedDate`, `supplierId`, `warehouseId`, `reference`, `note` or
  `lines` (one at a time) -> `400` with that key; with `"expectedDate": null` -> `200`. The draft is unchanged
  by the refused requests.
- AC-25 `DELETE /{id}` on a draft -> `204`; `GET /{id}` -> `404`.
- AC-26 Validation (each `400` with the stated key, nothing created): `lines` missing or `[]` -> `lines`;
  201 lines -> `lines`; `unitPrice` missing, `-0.01`, `"2"`, `1.0000001`, `1000000000` on the second line ->
  `lines[1].unitPrice`; `quantity` `0` -> `lines[0].quantity`; `supplierId` missing or `"abc"` ->
  `supplierId`; `orderDate` `"2026-02-30"` -> `orderDate`; `expectedDate` `"2026-10-08"` with `orderDate`
  `"2026-10-09"` -> `expectedDate`; a line (A, 100000, 100000) -> `lines[0].quantity` and
  `lines[0].unitPrice`. Accepted (`201`): `unitPrice` `0`; `expectedDate` equal to `orderDate`; a line
  (A, 99999.99, 100000).
- AC-27 References on create, nothing created: `supplierId` random UUID -> `409` `REFERENCE_NOT_FOUND` with
  key `supplierId`; inactive supplier -> `409` `REFERENCE_INACTIVE` with key `supplierId`; `CUS` -> `409`
  `PARTNER_ROLE_MISSING` with key `supplierId`; `warehouseId` random UUID / inactive -> `REFERENCE_NOT_FOUND`
  / `REFERENCE_INACTIVE` with key `warehouseId`; second line with a random article -> `REFERENCE_NOT_FOUND`
  with key `lines[1].articleId`; with service article `S` -> `ARTICLE_NOT_STOCKED`; B with `box` ->
  `UNIT_NOT_ON_ARTICLE` with key `lines[1].unitId`. A partner with both roles as supplier -> `201`.
- AC-28 Order of stages: random supplier and random warehouse and a service article -> `REFERENCE_NOT_FOUND`
  with key `supplierId` and without `warehouseId`; an invalid `unitPrice` together with a random supplier ->
  `400`.

Confirmation
- AC-30 `POST /{id}/confirm` on the draft of AC-20 -> `200`; `status == "confirmed"`,
  `number == "PO-000001"`, `confirmedAt` set, `confirmedBy ==` the acting key; the line has
  `outstandingBaseQuantity == 10`, `receivedBaseQuantity == 0`; `receiptStatus == "none"`; `updatedAt`
  unchanged. `GET /{id}` and `GET /purchase-orders/by-number/PO-000001` (and `po-000001`) return the same.
  Stock(A, W1) is still 0 and the ledger is empty.
- AC-31 Confirmed is immutable: `PUT` (valid body), `DELETE` and a second `confirm` -> `409` `INVALID_STATE`;
  the order is unchanged.
- AC-32 The factor is frozen at confirmation: a draft with (A, 5, box, 30) shows `baseQuantity == 60`. Set
  A / box to 10: the draft shows `factor == 10`, `baseQuantity == 50`, `lineAmount == 150`. Confirm ->
  `baseQuantity == 50`, `outstandingBaseQuantity == 50`. Set A / box to 20: the order still shows
  `factor == 10`, `baseQuantity == 50`.
- AC-33 Confirmation needs active masters and the role; each case -> `409` with the stated code and key, the
  order stays a draft with `number == null`: supplier deactivated -> `REFERENCE_INACTIVE` (`supplierId`);
  warehouse deactivated -> `REFERENCE_INACTIVE` (`warehouseId`); second article deactivated ->
  `REFERENCE_INACTIVE` (`lines[1].articleId`); supplier changed to customer only -> `PARTNER_ROLE_MISSING`
  (`supplierId`). After the master is restored the order confirms and gets `PO-000001` (the refused
  confirmations consumed no number).
- AC-34 Numbering: three orders confirmed one after another get `PO-000001`…`PO-000003`, independently of
  `SR-` numbers; a deleted draft and a draft left unconfirmed consume nothing. Ten drafts confirmed in parallel
  -> ten `200`, numbers exactly `PO-000001`…`PO-000010`. The same draft confirmed five times in parallel ->
  exactly one `200` and four `409 INVALID_STATE`.
- AC-35 A draft created by key K1 and confirmed by key K2 -> `createdBy == K1`, `updatedBy == K1`,
  `confirmedBy == K2`.

Goods receipt
- AC-40 PO [(A, 10, 2.5)]. `POST /stock-documents` `{ "type": "receipt", "documentDate": "2026-10-10",
  "warehouseId": W1, "purchaseOrderId": the order, "lines": [ { "articleId": A, "quantity": 4,
  "orderLineNo": 1 } ] }` -> `201`, `status == "draft"`, `purchaseOrder.id ==` the order's id,
  `purchaseOrder.number == "PO-000001"`, line `orderLineNo == 1`. Received(1) is 0, Outstanding(1) is 10,
  Stock(A, W1) is 0.
- AC-41 Post it -> `200`, `number == "SR-000001"`, `purchaseOrder` unchanged. Stock(A, W1) is 4; the ledger of
  the document has one entry `+4`; Received(1) is 4, Outstanding(1) is 6; `receiptStatus == "partial"`;
  the order's `status == "confirmed"` and `updatedAt` is unchanged.
- AC-42 Receive (1, 6) against the order -> Received(1) is 10, Outstanding(1) is 0,
  `receiptStatus == "full"`, `status == "confirmed"`; Stock(A, W1) is 10.
- AC-43 Never more than ordered: after AC-41 a draft receipt (1, 7) is created (`201`); posting it -> `409`
  `QUANTITY_EXCEEDS_ORDER` with key `lines[0].quantity`; it is still a draft with `number == null`;
  Stock(A, W1) is 4; Received(1) is 4; the ledger has no new entry. After `PUT` changes the quantity to 6 it
  posts with `SR-000002`.
- AC-44 Exact limit: PO [(A, 10, 1)]; receive (1, 10.000001) -> `QUANTITY_EXCEEDS_ORDER`; receive (1, 10) ->
  posted; a further receipt (1, 0.000001) -> `QUANTITY_EXCEEDS_ORDER`.
- AC-45 Several lines for one order line: PO [(A, 10, 1)]; a receipt with lines (A, 6, line 1),
  (A, 5, line 1) -> `QUANTITY_EXCEEDS_ORDER` with keys `lines[0].quantity` and `lines[1].quantity`; with
  (A, 6, line 1), (A, 4, line 1) -> posted, Received(1) is 10.
- AC-46 Several order lines: PO [(A, 10, 1), (B, 5, 2), (A, 3, 0)]. A receipt with lines (A, 3, line 3),
  (B, 6, line 2), (A, 2, line 1) -> `QUANTITY_EXCEEDS_ORDER` with key `lines[1].quantity` and without the
  other two; Stock(A, W1) is 0 (nothing was posted). With B's quantity 5 it posts: Received is 2, 5, 3 for
  lines 1, 2, 3; `receiptStatus == "partial"`. After receive (1, 8): `receiptStatus == "full"`.
- AC-47 Units: PO [(A, 5, box, 30)] (ordered 60). A receipt line (A, 2, box, line 1) posted -> Received(1) is
  24, ledger `+24`. A receipt line (A, 36, line 1) in `pcs` posted -> Received(1) is 60,
  `receiptStatus == "full"`. PO [(A, 60, 1)]: a receipt line (A, 5, box, line 1) -> posted, Received(1) is 60;
  (A, 6, box, line 1) on a new such order -> `QUANTITY_EXCEEDS_ORDER`.
- AC-48 Disagreement with the order, nothing created: a linked receipt for `W2` -> `409` `ORDER_MISMATCH` with
  key `warehouseId`; a line `{ articleId: B, orderLineNo: 1 }` where line 1 is A -> `ORDER_MISMATCH` with key
  `lines[0].articleId`; both at once -> both keys; `orderLineNo: 99` -> `409` `REFERENCE_NOT_FOUND` with key
  `lines[0].orderLineNo`.
- AC-49 Form of the link (each `400` with the stated key): `purchaseOrderId` on an `issue`, a `transfer` or a
  `count` -> `purchaseOrderId`; `"purchaseOrderId": "abc"` -> `purchaseOrderId`; a linked receipt with a line
  without `orderLineNo`, or with `orderLineNo` `0`, `1.5`, `"1"` -> `lines[0].orderLineNo`; an unlinked
  receipt with `"orderLineNo": 1` -> `lines[0].orderLineNo`. `"purchaseOrderId": null` and
  `"orderLineNo": null` on an unlinked receipt -> `201`. A random UUID as `purchaseOrderId` -> `409`
  `REFERENCE_NOT_FOUND` with key `purchaseOrderId`.
- AC-50 The link never changes: `PUT` on a linked draft with new lines (each with `orderLineNo`) -> `200`,
  `purchaseOrder` unchanged; `PUT` with `"purchaseOrderId"` in the body -> `400`; `PUT` with a line without
  `orderLineNo` -> `400` with key `lines[0].orderLineNo`; `PUT` moving it to `W2` -> `ORDER_MISMATCH`.
- AC-51 Unlinked documents do not count: with PO [(A, 10, 1)] open, an unlinked receipt of 5 of A into W1 is
  posted -> Stock(A, W1) is 5; Received(1) is 0; the document has `purchaseOrder == null` and
  `orderLineNo == null`.
- AC-52 Race: PO [(A, 10, 1)] and ten draft receipts (1, 3), all posted in parallel -> exactly three `200` and
  seven `409 QUANTITY_EXCEEDS_ORDER`, no other status; Received(1) is 9; Stock(A, W1) is 9; the numbers are
  `SR-000001`…`SR-000003`.
- AC-53 The supplier is not re-checked at posting: after PO, `SUP` is deactivated; receive (1, 4) -> posted.
  The warehouse deactivated instead -> `409` `REFERENCE_INACTIVE` with key `warehouseId` (005/R13).
- AC-54 `GET /stock-documents?purchaseOrderId=` returns exactly the documents linked to that order, drafts and
  posted; a random UUID -> `200`, empty; `abc` -> `400` with key `purchaseOrderId`.
- AC-55 **Received equals what was posted.** After at least five linked receipts against a three-line order
  (some in boxes, one reversed, one left as a draft), for every order line: Received(k) equals the sum of
  `baseQuantity` of the lines with `orderLineNo == k` over the documents of
  `GET /stock-documents?purchaseOrderId=&status=posted` whose `reversalOf` is `null`; Received(k) is between 0
  and the line's `baseQuantity`; and for every pair the ledger sum equals Stock(pair) (005/AC-60).

Close and reopen
- AC-60 PO [(A, 10, 1)], receive (1, 4). `POST /{id}/close` -> `200`; `status == "closed"`, `closedAt` set,
  `closedBy ==` the acting key; Received(1) is 4, Outstanding(1) is 0, `receiptStatus == "partial"`;
  `number` and lines unchanged. Stock(A, W1) is still 4.
- AC-61 Against a closed order: posting a draft receipt (1, 3) created before the close -> `409`
  `ORDER_NOT_OPEN` with key `purchaseOrderId`, it stays a draft; creating a linked receipt and `PUT` on the
  existing draft -> `ORDER_NOT_OPEN`; `GET` and `DELETE` of the draft -> `200` / `204`.
- AC-62 `POST /{id}/reopen` -> `200`; `status == "confirmed"`, `closedAt == null`, `closedBy == null`,
  Outstanding(1) is 6. A draft receipt (1, 3) left from before the close now posts; Received(1) is 7.
- AC-63 Transitions that are refused -> `409` `INVALID_STATE`, nothing changed: `close` of a draft and of a
  closed order; `reopen` of a draft and of a confirmed order; `confirm`, `PUT` and `DELETE` of a closed order.
- AC-64 Closing an order nothing was received against -> `closed`, `receiptStatus == "none"`. Closing a fully
  received order -> `closed`, `receiptStatus == "full"`.
- AC-65 Lists: `status=draft`, `confirmed`, `closed` and `receiptStatus=none`, `partial`, `full` filter and
  combine; `supplierId=` and `warehouseId=` filter; `search` matches a number (`po-0000`) and a draft's
  `reference` case-insensitively; newest first; each item has `lineCount`, `totalAmount`, `receiptStatus` and
  no `lines`; `status=posted` and `receiptStatus=all` -> `400` with that key.

Reversal
- AC-70 PO [(A, 10, 1)], receive (1, 4), then `POST /stock-documents/{receipt}/reverse` -> `201`; the
  reversing document has `purchaseOrder.id ==` the order's id and its line `orderLineNo == 1`; Received(1) is
  0, Outstanding(1) is 10, `receiptStatus == "none"`; Stock(A, W1) is 0. Afterwards receive (1, 10) -> posted.
- AC-71 A receipt against an order closed since can be reversed: `201`; Received(1) drops; the order is still
  `closed` with Outstanding(1) 0.
- AC-72 No negative stock by reversal: PO [(A, 10, 1)], receive (1, 10), issue 6 of A from W1; reversing the
  receipt -> `409` `INSUFFICIENT_STOCK`; Received(1) is still 10 and `receiptStatus == "full"`.

Incoming quantity
- AC-75 PO [(A, 10, 1)] into W1: `GET /stock-on-hand?articleId=A` has one item with `quantity == 0`,
  `incomingQuantity == 10`, `warehouse.id == W1`. After receive (1, 4): `quantity == 4`,
  `incomingQuantity == 6`. After receive (1, 6): `quantity == 10`, `incomingQuantity == 0`.
- AC-76 Incoming(A, W1) is the sum over confirmed orders: with PO [(A, 10, 1)] and PO [(A, 5, box, 30),
  (A, 2, 1)] -> 72; a draft order for A adds nothing; after the first order is closed -> 62; after it is
  reopened -> 72. An order into W2 counts for W2 only.
- AC-77 A pair with neither stock nor incoming quantity is not listed; every listed item has
  `incomingQuantity` (0 when nothing is on order). After a receipt is reversed, Incoming rises by its quantity.

Effects on masters
- AC-80 A partner on a draft order: `DELETE /partners/{id}` -> `409` `IN_USE`; after the draft is deleted ->
  `204`. On a confirmed or closed order: `DELETE` -> `IN_USE`; `PUT` renaming, deactivating, or changing the
  roles to customer only -> `200`, and the order still shows the partner as `supplier`.
- AC-81 A warehouse and an article named by a draft order: `DELETE` of each -> `409` `IN_USE`; after the draft
  is deleted -> `204`. An article on an order line: `PUT` changing `type` or `baseUnitId` -> `409` `IN_USE`
  with that key. (A line's unit of measure is always also the base unit or a conversion of its article, so
  the order's own hold on it is only observable as in AC-82.)
- AC-82 A draft order with a line in boxes: `DELETE /articles/{A}/units/{box}` -> `409` `IN_USE`. After the
  order is confirmed -> `204`, and the order line still shows `unit.code == "box"`, `factor == 12`. Then
  `DELETE /units-of-measure/{box}` -> `409` `IN_USE`: the confirmed order line still names the unit (R37).

MCP
- AC-85 `tools/list` returns exactly 45 names (literal list). Each of the eight new tools has a description, a
  closed `inputSchema` with described properties and `required` as in section 5, an `outputSchema` and its
  annotations; `purchase_order_confirm` has `destructiveHint == true`; the `lines` item of
  `purchase_order_create` requires `articleId`, `quantity`, `unitPrice`. `stock_document_create` has
  `purchaseOrderId`, its `lines` item has `orderLineNo`; `stock_document_list` has `purchaseOrderId`.
- AC-86 Full flow through tools only: `purchase_order_create` -> `draft`; `purchase_order_confirm` ->
  `number == "PO-000001"`, `confirmedBy ==` the MCP key; `stock_document_create` (linked receipt of part) and
  `stock_document_post` -> tool success; `purchase_order_get` by `number` -> `receiptStatus == "partial"` and
  the received quantity; `stock_on_hand_list` -> `quantity` and `incomingQuantity`; `purchase_order_close` ->
  `closed`; `purchase_order_reopen` -> `confirmed`. Each result equals HTTP for the corresponding request.
- AC-87 `purchase_order_list` with `{ "status": "confirmed", "receiptStatus": "partial" }`,
  `purchase_order_update` and `purchase_order_delete` on a draft (`{ "deleted": true }`) -> tool success,
  equal to HTTP.
- AC-88 Tool errors with the same `code` and `errors` keys as HTTP: `purchase_order_create` with `CUS` ->
  `PARTNER_ROLE_MISSING` (`supplierId`); with `unitPrice` `-1` -> `VALIDATION_FAILED` (`lines[0].unitPrice`);
  `purchase_order_update` / `_delete` / `_confirm` of a confirmed order -> `INVALID_STATE`;
  `purchase_order_reopen` of a confirmed order -> `INVALID_STATE`; `purchase_order_get` with `{}` ->
  `VALIDATION_FAILED` (`id`, `number`); `stock_document_post` of a receipt above the order ->
  `QUANTITY_EXCEEDS_ORDER` (`lines[0].quantity`); `stock_document_create` against a closed order ->
  `ORDER_NOT_OPEN` (`purchaseOrderId`); for another warehouse -> `ORDER_MISMATCH` (`warehouseId`);
  `partner_delete` of a supplier on an order -> `IN_USE`.

Tenant isolation (tenants X and Y, each with this spec's setup, its own key and MCP client)
- AC-90 X has a confirmed order with a receipt. Y: `GET /purchase-orders` -> empty, `total == 0`, also when
  filtered by X's supplier and warehouse ids; `GET /stock-documents?purchaseOrderId={X's order}` -> empty;
  `GET /stock-on-hand` -> empty. The same through the tools.
- AC-91 Y: `GET`, `PUT` (valid body), `DELETE`, `confirm`, `close` and `reopen` on X's draft, confirmed and
  closed orders, and `by-number/PO-000001` before Y has confirmed anything -> `404` `NOT_FOUND`; through tools
  -> tool error `NOT_FOUND`. X's orders are unchanged.
- AC-92 Y: an order with `supplierId` = X's supplier -> `409` `REFERENCE_NOT_FOUND` (`supplierId`); a receipt
  with `purchaseOrderId` = X's confirmed order -> `REFERENCE_NOT_FOUND` (`purchaseOrderId`); X's
  Received(1) is unchanged.
- AC-93 Per tenant: Y's first confirmed order is `PO-000001` although X has one; X's order for an article with
  code `A` gives Y's article with the same code no incoming quantity; Y's orders never make X's partner,
  warehouse or article used (X deletes unused ones of its own with the same codes -> `204`).

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on "Goods receipt", "Close and reopen", "Reversal" and "Incoming quantity": received equals
  what was posted (AC-55), never more than ordered (AC-43–AC-47, AC-52), a refused posting changes nothing,
  and close / reopen / reversal move the outstanding quantity as stated.
- Approved changes to earlier tests are listed in AC-01; change nothing else there. Requests of specs 005–008
  that send neither `purchaseOrderId` nor `orderLineNo` must behave exactly as before.
- Numbers are per tenant: a fresh tenant per test gives `PO-000001` and `SR-000001`.

Builder
- Posting a linked receipt is the posting of spec 005 with one more lock and one more check. Lock order, the
  same for every posting and reversal: document -> order -> (article, warehouse) pairs -> counter. Compare
  with the outstanding quantities under the lock on the order; without it AC-52 fails. Close and reopen take
  the same lock. Under the per-tenant lock accepted for the MVP (ADR-0012, amendment of 2026-10-09) confirm,
  close, reopen and every posting and reversal simply run inside it; no lock order is needed.
- The received quantity may be a sum over posted lines or a maintained column on the order line; if a column,
  it is written in the posting and reversal transactions and AC-55 must hold. It is not an "edit" of the
  confirmed order in the sense of S3 — state in the model which columns are progress and which are frozen.
- One amount function and one progress function in Domain (AC-03). Spec 010 reuses both, the order lifecycle
  and the link checks for sales orders: build them so that a second order kind is a second user, not a copy.
- `incomingQuantity` joins stock on hand in one query with correct `total` and paging; the ordering of 005
  §4.2 is unchanged.
- The "used" checks (R37, R38) extend the existing `IN_USE` mechanism, including the raced case.
- As built (spec 005), `DocumentCounter.Start` takes a `StockDocumentType` and `DocumentNumber` knows stock
  prefixes only; the counter's key is already a text. Generalise both to a document kind (`purchaseOrder`,
  later `salesOrder`) rather than adding order kinds to the stock document type.
- `receiptStatus` is a list filter with paging (R36): it has to be decidable in SQL. A maintained received
  quantity on the order line makes that a plain predicate; a sum over posted lines works too, but then inside
  the list query.
- Annotations as built: `purchase_order_confirm` has those of `stock_document_post`; `_close` and `_reopen`
  have those of a create.
- Anything unclear or contradictory: `docs/questions/009-q.md`, then continue with the rest.
