# Spec 010 — Sales orders and delivery

Status: ready for the tester and the builder once spec 009 is merged to `main`.
Branches: `tests/010-sales-orders-delivery` (tester), `feat/010-sales-orders-delivery` (builder).
Read first: **spec 009** (this spec mirrors it and repeats as little of it as possible), specs 005–007,
ADR-0016, **ADR-0017**. Why this is the tenth spec: `docs/roadmap.md` section 3 — the mirror of 009 plus
availability.

**Inherited, not re-specified** (roadmap §4): everything specs 001–009 established — transport, errors,
references, MCP mapping, stock documents, posting, reversal, units, numbering, the ledger, and the order
mechanics of spec 009. Numbers are local; "009/R26" means rule R26 of spec 009.

**Mirrored from spec 009.** Where this spec says a rule or criterion of spec 009 applies *mirrored*, read it
with these substitutions, and nothing else changed:

| Spec 009 | Spec 010 |
|---|---|
| purchase order, `PurchaseOrder`, `/purchase-orders`, `purchase_order_*` | sales order, `SalesOrder`, `/sales-orders`, `sales_order_*` |
| supplier, `supplierId`, `supplier`, role `isSupplier` | customer, `customerId`, `customer`, role `isCustomer` |
| partner `SUP` (has the role), partner `CUS` (lacks it) | partner `CUS` (has the role), partner `SUP` (lacks it) |
| `expectedDate` | `requestedDate` |
| number `PO-…`, counter type `purchaseOrder` | number `SO-…`, counter type `salesOrder` |
| goods receipt: stock document `type` `receipt`, `purchaseOrderId`, `purchaseOrder`, number `SR-…` | delivery: stock document `type` `issue`, `salesOrderId`, `salesOrder`, number `SI-…` |
| `receivedBaseQuantity`, `receiptStatus` | `deliveredBaseQuantity`, `deliveryStatus` |
| `incomingQuantity` | `reservedQuantity` |

## 1. Goal

Let a tenant take a customer's order and deliver it, in one or several shipments, knowing how much of each
line has left — and let anyone see how much stock is promised and how much is really free.

- A **sales order** names a customer, the warehouse it ships from and lines with article, unit, quantity and
  unit price. A draft is the offer; **confirming** it is accepting it.
- A **delivery** is a stock document of type `issue` linked to the order. Posting it takes the goods out of
  stock exactly as any issue does — never more than is on hand, never more than was ordered.
- The outstanding quantity of confirmed orders is **reserved**; stock on hand shows it and the **available**
  quantity. Reservation informs; it does not block (ADR-0017).

## 2. Scope

In scope
1. `SalesOrder` with lines; operations list, get, get by number, create, replace, delete, confirm, close,
   reopen — each as an HTTP endpoint and an MCP tool.
2. The link from a stock document of type `issue` to a sales order and its lines.
3. `reservedQuantity` and `availableQuantity` in stock on hand.
4. Further `IN_USE` cases for the masters, as in spec 009.

Out of scope
- A quotation document; hard reservation or allocation of stock to an order; an availability check at
  confirmation; back-order handling beyond "the delivery is refused until stock is there" (ADR-0017).
- Delivery from another warehouse than the order's; over-delivery; partial returns (a whole delivery is undone
  by reversal, 006); a delivery address other than the partner's address; shipping costs, carriers.
- Everything out of scope in spec 009: editing a confirmed order, `service` articles, taxes, discounts,
  currencies, price lists, credit limits, invoices (014), cost of goods sold (011).

## 3. Data

`SalesOrder` and `SalesOrderLine`: the columns, keys and indexes of `PurchaseOrder` and `PurchaseOrderLine`
(009 §3) mirrored — `CustomerId` for `SupplierId`, `RequestedDate` for `ExpectedDate`.
`StockDocument` gains `SalesOrderId` (nullable). `StockDocumentLine.OrderLineNo` (009) serves both links.
`DocumentCounter` gets the type `salesOrder`. All new foreign keys include `TenantId` and restrict deletes.
One migration.

## 4. Operations — HTTP

### 4.1 Sales orders

Representation (`SalesOrder`): that of 009 §4.1 mirrored —
```json
{ "id": "uuid", "status": "confirmed", "number": "SO-000001",
  "orderDate": "2026-10-09", "requestedDate": null,
  "customer": { "id": "uuid", "code": "CUS", "name": "Acme d.o.o." },
  "warehouse": { "id": "uuid", "code": "WH-1", "name": "Main warehouse" },
  "reference": null, "note": null,
  "deliveryStatus": "partial",
  "lines": [
    { "lineNo": 1, "article": { … }, "unit": { … }, "quantity": 40,
      "unitPrice": 4.5, "lineAmount": 180,
      "factor": 1, "baseUnit": { … }, "baseQuantity": 40,
      "deliveredBaseQuantity": 25, "outstandingBaseQuantity": 15 } ],
  "totalAmount": 180,
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid",
  "confirmedAt": "…Z", "confirmedBy": "uuid", "closedAt": null, "closedBy": null }
```

Operations: the nine of 009 §4.1 mirrored, with the same bodies, successes and errors:
`GET /sales-orders?status=&deliveryStatus=&customerId=&warehouseId=&search=&limit=&offset=`,
`GET /sales-orders/{id}`, `GET /sales-orders/by-number/{number}`, `POST /sales-orders`,
`PUT /sales-orders/{id}`, `DELETE /sales-orders/{id}`, `POST /sales-orders/{id}/confirm`, `/close`, `/reopen`.

### 4.2 Changes to the stock document

| Operation | Change |
|---|---|
| Create | Body gains `"salesOrderId"?` (uuid or `null`). A line's `"orderLineNo"` (009) is used with it. |
| Replace | `salesOrderId` is **not** part of the replace body. |
| List | New filter `salesOrderId=`. |
| Post | `ORDER_NOT_OPEN` and `QUANTITY_EXCEEDS_ORDER` (009) also for a linked issue, before `INSUFFICIENT_STOCK`. |
| Reverse | Works for a linked issue (R14). |

Every stock document and list summary gains `"salesOrder"`: `{ "id", "number" }` on a linked document,
otherwise `null`. `errors` keys of `ORDER_NOT_OPEN` and of an unknown order are `salesOrderId`.

### 4.3 Stock on hand

Every item gains `"reservedQuantity"` and `"availableQuantity"` (R15, R16), in base units. The list has one
item per (article, warehouse) pair whose `quantity`, `incomingQuantity` **or** `reservedQuantity` is not zero.
The ordering of 005 §4.2 (article code, then warehouse code) holds for every listed pair.

### 4.4 Masters and error codes

- 009 §4.4 applies mirrored: partner, warehouse, article, unit of measure and (for drafts) article unit are
  `IN_USE` while a sales order names them; an order line freezes its article's `type` and `baseUnitId`.
- No new error code. `PARTNER_ROLE_MISSING` is used with key `customerId`; `ORDER_NOT_OPEN` with key
  `salesOrderId`; `ORDER_MISMATCH` and `QUANTITY_EXCEEDS_ORDER` as in spec 009; `INVALID_STATE` for the order
  transitions as in spec 009.

## 5. Operations — MCP

- Eight tools, those of 009 §5 mirrored, with the same arguments, annotations and error codes:
  `sales_order_list` (`{ status?, deliveryStatus?, customerId?, warehouseId?, search?, limit?, offset? }`),
  `sales_order_get`, `sales_order_create`, `sales_order_update`, `sales_order_delete`, `sales_order_confirm`,
  `sales_order_close`, `sales_order_reopen`.
- Descriptions say, in addition to the mirrored text of 009 §5:
  - `sales_order_create` — a draft is an offer and reserves nothing; `unitPrice` is the selling price per unit
    of the line, without tax; the customer must be a partner with `isCustomer`.
  - `sales_order_confirm` — permanent; from then on the outstanding quantity is reserved in the order's
    warehouse; confirmation is not refused when stock is short — check `availableQuantity` with
    `stock_on_hand_list`.
  - `sales_order_get` — how to deliver: create a stock document of type `issue` with `salesOrderId`, the
    order's warehouse and lines with `articleId`, `quantity`, `orderLineNo`, then post it.
- Changed tools: `stock_document_create` gains `salesOrderId?` (uuid or null); `stock_document_list` gains
  `salesOrderId?`. The description of `stock_document_post` says that a delivery can fail with
  `INSUFFICIENT_STOCK` although the order is confirmed, and that an unlinked issue can take reserved stock.
  `stock_on_hand_list` describes the four quantities. The `*_delete` descriptions of the masters mention the
  further `IN_USE` case.
- `tools/list` returns exactly 53 tools: the 45 of spec 009 and these 8.

## 6. Business rules

The order
- R1. 009/R1–R17 apply mirrored: customer and warehouse references and the role check
  (`PARTNER_ROLE_MISSING`, key `customerId`); dates (`requestedDate` — when the customer wants the goods — not
  earlier than `orderDate`); lines, units, quantities; `unitPrice` and amounts; order of checks; the draft;
  confirmation and its checks; `SO-` numbering; immutability; close and reopen; audit fields.
- R2. `warehouseId` of a sales order is the warehouse the goods ship from.
- R3. A partner may be supplier on purchase orders and customer on sales orders at the same time; each order
  checks only its own role.
- R4. **Confirmation is never refused for stock reasons.** An order may be confirmed for more than is on hand
  or available, in any number.

Delivery — the link
- R5. 009/R18–R23 apply mirrored: only an `issue` may carry `salesOrderId` (`400` on any other type); the link
  is set at creation and is not part of the replace body; every line of a linked document needs `orderLineNo`;
  saving checks that the order exists (`REFERENCE_NOT_FOUND`), is `confirmed` (`ORDER_NOT_OPEN`), that every
  `orderLineNo` is a line of it, and that warehouse and articles agree (`ORDER_MISMATCH`); any unit of the
  article; several lines per order line; quantities are not compared at saving.
- R6. A stock document has at most one order link: `purchaseOrderId` belongs to receipts, `salesOrderId` to
  issues. `orderLineNo` is allowed exactly when the document has one of them (009/R20).

Delivery — posting
- R7. Posting a linked issue is the posting of specs 005–007 with the two order checks of spec 009. Order of
  checks: document exists -> is a draft -> warehouse and articles active -> conversion -> the order is
  `confirmed` (`ORDER_NOT_OPEN`, key `salesOrderId`) -> the order's quantities (`QUANTITY_EXCEEDS_ORDER`,
  009/R26 mirrored) -> **stock** (`INSUFFICIENT_STOCK`, 005/R15). The customer is not re-checked.
- R8. `deliveredBaseQuantity`, `outstandingBaseQuantity` and `deliveryStatus` are 009/R25, R27 and R29
  mirrored: delivered = the sum of `baseQuantity` of the lines naming the order line on `posted`, not
  reversing, stock documents; always `0 ≤ deliveredBaseQuantity ≤ baseQuantity`.
- R9. **Sufficiency is against stock on hand**, exactly as for any issue (005/R15, R16): the sum of the
  document's base quantities per article must not exceed the stock on hand of that article in the warehouse.
  The reserved quantity plays no part — neither this order's nor another's.
- R10. A refused posting changes nothing: no entry, no number, no delivered quantity; the draft can be
  corrected or posted later.
- R11. A posted delivery writes the ledger entries of an issue (`−baseQuantity` per line) and gets an `SI-`
  number.
- R12. Concurrency: 009/R30 mirrored for the order's quantities, and 005/R16 for stock — of deliveries for
  several orders competing for the same stock, only as many succeed as stock covers.
- R13. 009/R31 mirrored: against a draft or closed order nothing can be saved or posted.

Delivery — reversal
- R14. 009/R32–R34 mirrored: the reversing document carries `salesOrder` and `orderLineNo`; the goods return
  to stock; the delivered quantity of the order lines drops, and while the order is `confirmed` its
  outstanding — and therefore the reserved — quantity rises again. Reversing a delivery is never refused for
  stock (006/R16) and does not depend on the order's status.

Reserved and available quantity
- R15. `reservedQuantity` of an (article, warehouse) pair = the sum of `outstandingBaseQuantity` over the
  lines with that article on `confirmed` sales orders shipping from that warehouse. Drafts and closed orders
  reserve nothing.
- R16. `availableQuantity` = `quantity − reservedQuantity`. It may be negative. `incomingQuantity` is not part
  of it.
- R17. **Reservation informs, it does not block** (ADR-0017): an unlinked issue, a transfer and a delivery for
  another order are judged against stock on hand only and may take goods a confirmed order counts on. The
  order then stays outstanding and its delivery is refused until stock is there.
- R18. Consequences that always hold:
  - confirming an order line of base quantity q raises `reservedQuantity` by q and leaves `quantity`;
  - posting a delivery of base quantity d lowers `quantity` and `reservedQuantity` by d each:
    `availableQuantity` is unchanged;
  - closing an order lowers `reservedQuantity` by its outstanding quantities; reopening raises it again;
  - `quantity` is still exactly the sum of the ledger (005/R19) and never negative.
- R19. Stock on hand lists a pair when any of `quantity`, `incomingQuantity`, `reservedQuantity` is not zero;
  every item has all four quantities.

Reading and masters
- R20. Order list: 009/R36 mirrored (`deliveryStatus`, `customerId`).
- R21. 009/R37 and R38 apply mirrored.

## 7. Edge cases

- E1. 009/E1–E8, E17 and E18 apply mirrored.
- E2. An order for 10 with nothing in stock -> confirms; `reservedQuantity` 10, `availableQuantity` −10; the
  pair is listed although `quantity` is 0.
- E3. A delivery above the outstanding quantity **and** above stock -> `QUANTITY_EXCEEDS_ORDER` (checked
  first).
- E4. A delivery within the order but above stock -> `INSUFFICIENT_STOCK` with the short lines' keys; the
  covered lines are not posted either (005/E7).
- E5. A delivery in boxes against a line ordered in pieces, and the reverse: base quantities are compared,
  with the order and with stock.
- E6. Two delivery lines for one order line, together above the outstanding quantity ->
  `QUANTITY_EXCEEDS_ORDER` with both keys.
- E7. Stock in another warehouse does not serve a delivery: the order ships from its own warehouse; transfer
  first.
- E8. A fully delivered order: `reservedQuantity` contribution 0; a further linked issue ->
  `QUANTITY_EXCEEDS_ORDER`; after a delivery is reversed the quantity can be delivered again.
- E9. Closing a partly delivered order frees the rest of its reservation; draft deliveries cannot be posted
  until a reopen.
- E10. A partner with both roles: supplier on a purchase order and customer on a sales order.
- E11. `salesOrderId` and `purchaseOrderId` both non-null in one create body -> `400` (one of them is on the
  wrong type whatever the type is). `errors` has the key of every link the type cannot carry:
  `purchaseOrderId` on an `issue`, `salesOrderId` on a `receipt`, both on a `transfer` or a `count`.

## 8. Tenant isolation

- T1. 009/T1–T5 apply mirrored: sales orders are tenant-owned; another tenant's order is `404`; another
  tenant's order as `salesOrderId` is `REFERENCE_NOT_FOUND`; the counter is per tenant.
- T2. Reserved and available quantities are computed from the caller's tenant's orders and ledger only.

## 9. Security requirements

- S1. Any tenant key may create, confirm, close and reopen sales orders and post deliveries (restrictable
  from spec 017); `confirmedBy`, `closedBy` and the delivery's `postedBy` attribute each step.
- S2. A confirmed sales order promises goods to a third party; the ledger is still written only by posting a
  stock document. 009/S3 (immutability through the DbContext) and S4 (no prices in logs) apply mirrored.
- S3. Customer names, addresses and what they bought are tenant data that can be personal data; they are
  never logged and never appear in another tenant's errors.

## 10. Acceptance criteria

Conventions and setup as in spec 009 §10 (standard setup, `box` with A / box = 12, partners `SUP` and `CUS`).
- "SO [lines]" means: create a sales order for `CUS` and `W1`, dated `2026-10-09`, with those lines (article,
  quantity, unit when not base, price), and confirm it.
- "Deliver (k, n) against the order" means: create an issue for `W1` with `salesOrderId` and one line
  `{ articleId: the article of order line k, quantity: n, orderLineNo: k }`, and post it.
- "Delivered(k)", "Outstanding(k)": `deliveredBaseQuantity`, `outstandingBaseQuantity` of order line k.
  "Reserved(A, W1)", "Available(A, W1)": `reservedQuantity`, `availableQuantity` of the pair in
  `GET /stock-on-hand`, or 0 when the pair is absent.
- "Receive n of A into W1" is the unlinked receipt of spec 005.
Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The only earlier tests changed are:
  the literal tool list; tests asserting the exact property set of a stock document, of its list summary or of
  a stock-on-hand item (they gain `salesOrder`, `reservedQuantity`, `availableQuantity`); tests pinning the
  exact input-schema properties of `stock_document_create` or `stock_document_list`; data-driven
  schema tests of spec 009 may gain rows for the sales tools, their assertions unchanged. No test of spec 009
  that asserts behaviour changes. One migration added.
- AC-02 *(builder)* 009/AC-02 mirrored.
- AC-03 *(builder, unit)* The amount, progress and status rules are the same code as for purchase orders
  (one implementation, two users); the reserved / available rule (R15, R16) is unit-tested without HTTP.

Mirrored criteria — the order itself
- AC-10, AC-11 — 009/AC-10 and AC-11 mirrored (smoke).
- AC-20 … AC-28 — 009/AC-20 … AC-28 mirrored (drafts: creation, no effect, lines and amounts, replace,
  required fields, delete, validation, references and the role, order of stages). In AC-21 "Incoming" reads
  "Reserved" and the refused document is an issue with `salesOrderId`.
- AC-30 … AC-35 — 009/AC-30 … AC-35 mirrored (confirmation, immutability, frozen factor, active masters and
  role, numbering and its races, attribution). In AC-33 the customer is changed to supplier only; in AC-34
  the independent series is `SI-`.
- AC-80 … AC-82 — 009/AC-80 … AC-82 mirrored (effects on masters). In AC-80 the roles are changed to supplier
  only and the order still shows the partner as `customer`.

Delivery
- AC-40 Receive 100 of A into W1. SO [(A, 40, 4.5)]. `POST /stock-documents` `{ "type": "issue",
  "documentDate": "2026-10-10", "warehouseId": W1, "salesOrderId": the order, "lines": [ { "articleId": A,
  "quantity": 25, "orderLineNo": 1 } ] }` -> `201`, `status == "draft"`, `salesOrder.id ==` the order's id,
  `salesOrder.number == "SO-000001"`, `purchaseOrder == null`, line `orderLineNo == 1`. Delivered(1) is 0,
  Outstanding(1) is 40, Stock(A, W1) is 100.
- AC-41 Post it -> `200`, `number == "SI-000001"`. Stock(A, W1) is 75; the ledger of the document has one
  entry `-25`; Delivered(1) is 25, Outstanding(1) is 15; `deliveryStatus == "partial"`; the order is still
  `confirmed` and its `updatedAt` unchanged.
- AC-42 Deliver (1, 15) -> Delivered(1) is 40, Outstanding(1) is 0, `deliveryStatus == "full"`,
  `status == "confirmed"`; Stock(A, W1) is 60.
- AC-43 Never more than ordered: after AC-41, a draft delivery (1, 16) is created (`201`); posting -> `409`
  `QUANTITY_EXCEEDS_ORDER` with key `lines[0].quantity`; it is still a draft with `number == null`;
  Stock(A, W1) is 75; Delivered(1) is 25. After `PUT` changes the quantity to 15 it posts.
- AC-44 Never more than is there: receive 10 of A into W1; SO [(A, 40, 1)]; deliver (1, 11) -> `409`
  `INSUFFICIENT_STOCK` with key `lines[0].quantity`; the document stays a draft; Stock(A, W1) is 10;
  Delivered(1) is 0; no number was consumed. Deliver (1, 10) -> posted, Stock(A, W1) is 0, Delivered(1) is 10,
  Outstanding(1) is 30. After receiving 30 more, deliver (1, 30) -> `deliveryStatus == "full"`.
- AC-45 Order before stock in the checks: with 10 of A in W1 and SO [(A, 5, 1)], a delivery (1, 20) -> `409`
  `QUANTITY_EXCEEDS_ORDER` (not `INSUFFICIENT_STOCK`).
- AC-46 Several order lines, one short: receive 100 of A into W1, nothing of B; SO [(A, 10, 1), (B, 5, 2)].
  A delivery with lines (A, 10, line 1), (B, 5, line 2) -> `INSUFFICIENT_STOCK` with key `lines[1].quantity`
  and without `lines[0].quantity`; Stock(A, W1) is 100, Delivered(1) is 0 (nothing was posted). A delivery
  with only (A, 10, line 1) posts; `deliveryStatus == "partial"`.
- AC-47 Several lines for one order line: with stock, SO [(A, 10, 1)]; a delivery with (A, 6, line 1),
  (A, 5, line 1) -> `QUANTITY_EXCEEDS_ORDER` with both keys; with (A, 6, line 1), (A, 4, line 1) -> posted.
- AC-48 Units: receive 100 of A into W1. SO [(A, 5, box, 60)] (ordered 60). A delivery line (A, 2, box,
  line 1) posted -> ledger `-24`, Delivered(1) is 24. A delivery line (A, 36, line 1) posted -> Delivered(1)
  is 60, Stock(A, W1) is 40. A delivery of 4 box against SO [(A, 40, 1)] -> `QUANTITY_EXCEEDS_ORDER` (48 > 40).
- AC-49 Disagreement and form — 009/AC-48, AC-49, AC-50 and AC-54 mirrored: another warehouse / another
  article -> `ORDER_MISMATCH`; unknown `orderLineNo` -> `REFERENCE_NOT_FOUND`; `salesOrderId` on a `receipt`,
  `transfer` or `count` -> `400` with key `salesOrderId`; line without `orderLineNo` -> `400`; the link is not
  part of `PUT`; `GET /stock-documents?salesOrderId=` returns exactly the linked documents. In addition:
  a body with both `purchaseOrderId` and `salesOrderId` set -> `400`; a linked issue for a **purchase**
  order's id as `salesOrderId` -> `409` `REFERENCE_NOT_FOUND` with key `salesOrderId`.
- AC-50 Stock elsewhere does not serve: 50 of A in W2, none in W1; SO [(A, 5, 1)] from W1; deliver (1, 5) ->
  `INSUFFICIENT_STOCK`. After a transfer of 5 from W2 to W1 the same draft posts.
- AC-51 Race on the order: receive 100 of A into W1; SO [(A, 10, 1)]; ten draft deliveries (1, 3) posted in
  parallel -> exactly three `200` and seven `409 QUANTITY_EXCEEDS_ORDER`, no other status; Delivered(1) is 9;
  Stock(A, W1) is 91.
- AC-52 Race on stock, two orders: receive 10 of A into W1; two orders SO [(A, 10, 1)]; one draft delivery
  (1, 10) for each, posted in parallel -> exactly one `200` and one `409 INSUFFICIENT_STOCK`; Stock(A, W1) is
  0; one order is `full`, the other `none`. Repeated at least ten times.
- AC-53 The customer is not re-checked at posting: after SO, `CUS` is deactivated; deliver (1, 5) -> posted.
- AC-54 **Delivered equals what was posted.** After at least five linked issues against a three-line order
  (some in boxes, one reversed, one left as a draft), for every order line: Delivered(k) equals the sum of
  `baseQuantity` of the lines with `orderLineNo == k` over the documents of
  `GET /stock-documents?salesOrderId=&status=posted` whose `reversalOf` is `null`; Delivered(k) is between 0
  and the line's `baseQuantity`; for every pair the ledger sum equals Stock(pair).

Close and reopen
- AC-60 Receive 100 of A into W1; SO [(A, 10, 1)]; deliver (1, 4). `POST /{id}/close` -> `200`;
  `status == "closed"`, `closedAt` set, `closedBy ==` the acting key; Delivered(1) is 4, Outstanding(1) is 0,
  `deliveryStatus == "partial"`; Stock(A, W1) is 96; Reserved(A, W1) is 0.
- AC-61 Against a closed order: posting a draft delivery (1, 3) created before the close -> `409`
  `ORDER_NOT_OPEN` with key `salesOrderId`, it stays a draft; creating a linked issue and `PUT` on the draft
  -> `ORDER_NOT_OPEN`; `DELETE` of the draft -> `204`.
- AC-62 `POST /{id}/reopen` -> `200`; `status == "confirmed"`, `closedAt == null`, Outstanding(1) is 6,
  Reserved(A, W1) is 6. A draft delivery (1, 3) left from before the close now posts; Delivered(1) is 7.
- AC-63 Refused transitions -> `409` `INVALID_STATE`, nothing changed: `close` of a draft and of a closed
  order; `reopen` of a draft and of a confirmed order; `confirm`, `PUT`, `DELETE` of a closed order.
- AC-64 Lists — 009/AC-65 mirrored (`deliveryStatus`, `customerId`).

Reversal
- AC-70 Receive 100 of A into W1; SO [(A, 10, 1)]; deliver (1, 4); reverse the delivery -> `201`; the
  reversing document has `salesOrder.id ==` the order's id and line `orderLineNo == 1`; its ledger entry is
  `+4`; Stock(A, W1) is 100; Delivered(1) is 0, Outstanding(1) is 10, `deliveryStatus == "none"`;
  Reserved(A, W1) is 10. Afterwards deliver (1, 10) -> posted.
- AC-71 A delivery against an order closed since can be reversed: `201`; Delivered(1) drops; the order is
  still `closed`; Reserved(A, W1) is still 0.
- AC-72 A reversing delivery document and the reversed original are immutable and cannot be reversed again
  (`409 INVALID_STATE`, 006/R18).

Reserved and available quantity
- AC-75 Receive 100 of A into W1. A draft sales order for 30 of A: Reserved(A, W1) is 0. Confirm it:
  `GET /stock-on-hand?articleId=A` has one item with `quantity == 100`, `reservedQuantity == 30`,
  `availableQuantity == 70`, `incomingQuantity == 0`.
- AC-76 A delivery leaves the available quantity unchanged: after AC-75, deliver (1, 12) -> `quantity == 88`,
  `reservedQuantity == 18`, `availableQuantity == 70`. An unlinked issue of 8 -> `quantity == 80`,
  `reservedQuantity == 18`, `availableQuantity == 62`.
- AC-77 Reserved is the sum over confirmed orders: SO [(A, 30, 1)] and SO [(A, 2, box, 50), (A, 6, 1)] ->
  Reserved(A, W1) is 60. Closing the first -> 30; reopening it -> 60. An order shipping from W2 reserves in W2
  only.
- AC-78 Selling before buying: with no stock, SO [(A, 10, 1)] -> confirmation `200`; the pair (A, W1) is
  listed with `quantity == 0`, `reservedQuantity == 10`, `availableQuantity == -10`; deliver (1, 10) -> `409`
  `INSUFFICIENT_STOCK`. After PO [(A, 10, 1)] is confirmed: `incomingQuantity == 10`,
  `availableQuantity == -10`. After the goods are received against it: `quantity == 10`,
  `incomingQuantity == 0`, `availableQuantity == 0`; the draft delivery now posts and the pair is no longer
  listed.
- AC-79 Reservation does not block: receive 10 of A into W1; SO [(A, 10, 1)] (Available is 0). An unlinked
  issue of 10 posts (`200`); Stock(A, W1) is 0, Reserved(A, W1) is 10, Available(A, W1) is −10; deliver
  (1, 10) -> `INSUFFICIENT_STOCK`; the order is still `confirmed` with Outstanding(1) 10. Likewise a transfer
  of reserved stock to W2 posts.

MCP
- AC-85 `tools/list` returns exactly 53 names (literal list). The eight new tools have description, closed
  `inputSchema` with described properties and `required` as their mirrors in spec 009, `outputSchema` and
  annotations; `sales_order_confirm` has `destructiveHint == true`. `stock_document_create` and
  `stock_document_list` have `salesOrderId`.
- AC-86 Full flow through tools only, with stock received first: `sales_order_create` -> `draft`;
  `sales_order_confirm` -> `number == "SO-000001"`, `confirmedBy ==` the MCP key; `stock_on_hand_list` ->
  `reservedQuantity`, `availableQuantity`; `stock_document_create` (linked issue of part) and
  `stock_document_post` -> tool success; `sales_order_get` by `number` -> `deliveryStatus == "partial"`;
  `stock_document_reverse` of the delivery -> tool success, `deliveryStatus == "none"`; `sales_order_close`,
  `sales_order_reopen` -> tool success. Each result equals HTTP for the corresponding request.
- AC-87 `sales_order_list` with `{ "status": "confirmed", "deliveryStatus": "none" }`, `sales_order_update`
  and `sales_order_delete` on a draft -> tool success, equal to HTTP.
- AC-88 Tool errors with the same `code` and `errors` keys as HTTP: `sales_order_create` with `SUP` ->
  `PARTNER_ROLE_MISSING` (`customerId`); `sales_order_update` / `_delete` / `_confirm` of a confirmed order ->
  `INVALID_STATE`; `sales_order_close` of a draft -> `INVALID_STATE`; `stock_document_post` of a delivery
  above the order -> `QUANTITY_EXCEEDS_ORDER` (`lines[0].quantity`); above stock -> `INSUFFICIENT_STOCK`
  (`lines[0].quantity`); `stock_document_create` against a closed order -> `ORDER_NOT_OPEN`
  (`salesOrderId`); `partner_delete` of a customer on an order -> `IN_USE`.

Tenant isolation (tenants X and Y, each with this spec's setup)
- AC-90 … AC-92 — 009/AC-90 … AC-92 mirrored (X's orders are invisible to Y over HTTP and tools; every
  operation on them is `404` / `NOT_FOUND`; X's customer and X's order as references are
  `REFERENCE_NOT_FOUND`).
- AC-93 Per tenant: Y's first confirmed sales order is `SO-000001` although X has one; X's confirmed order for
  an article with code `A` gives Y's article with the same code `reservedQuantity == 0` (Y's pair with stock
  shows `availableQuantity == quantity`); Y's unlinked issue of its own stock is not refused and X's
  Reserved is unchanged.

Purchase to sale
- AC-95 One tenant, through HTTP only: PO [(A, 5, box, 30)]; receive (1, 5 box) against it; SO [(A, 40, 4.5)];
  deliver (1, 25); deliver (1, 15). Then: the ledger of (A, W1) is `+60`, `-25`, `-15` in that order;
  Stock(A, W1) is 20; `incomingQuantity == 0`, `reservedQuantity == 0`, `availableQuantity == 20`; the
  purchase order has `receiptStatus == "full"`, the sales order `deliveryStatus == "full"`,
  `totalAmount == 180`. Reverse the second delivery: Stock(A, W1) is 35, Delivered(1) is 25,
  `reservedQuantity == 15`, `availableQuantity == 20`.

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on "Delivery" and "Reserved and available quantity": the two limits and their order
  (AC-43–AC-46), a refused posting changes nothing, the two races (AC-51, AC-52), the available quantity
  unchanged by a delivery (AC-76), and reservation that informs without blocking (AC-78, AC-79).
- Mirrored criteria are written by applying the substitution table to the spec 009 criterion; if a mirrored
  criterion does not make sense after substitution, ask in `docs/questions/010-q.md` rather than guess.
- Approved changes to earlier tests are listed in AC-01; change nothing else there.

Builder
- A sales order is the second user of the order lifecycle, amount, progress and link code of spec 009
  (AC-03). If spec 009's code has to be reshaped to be shared, do that first, with spec 009's tests green.
- Posting a linked issue: lock order document -> order -> pairs -> counter, as in spec 009; check the order's
  quantities, then stock, both under the locks (or under the per-tenant lock, as spec 009 notes). AC-52 is
  the stock race of 005/AC-46 reached through two orders.
- `reservedQuantity` joins stock on hand like `incomingQuantity`: one query, correct `total` and paging, pairs
  with only a reservation included.
- Anything unclear or contradictory: `docs/questions/010-q.md`, then continue with the rest.
