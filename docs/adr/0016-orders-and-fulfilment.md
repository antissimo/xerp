# ADR-0016: Orders are confirmed commitments; fulfilment is a stock document linked to the order

Status: accepted (2026-10-09) — continues ADR-0007, ADR-0012, ADR-0013 and ADR-0014; first applied by spec 009
(purchase orders), then by spec 010 (sales orders). Points 2, 4, 6, 8, 9 and 11 are the architect's defaults
and are listed for the owner in the hand-over of spec 009.

## Context
Until now stock moves only through manual stock documents. Buying and selling add a document that moves
nothing — the order — and a need to know, per order line, how much of it has arrived or left. Open questions:
what an order is in data, how a goods receipt or a delivery relates to it, what "partial" means when units
differ, and what may still change once an order is agreed with the other party.

## Decision
1. **An order is its own resource** (`PurchaseOrder`, `SalesOrder`), not a type of stock document. It has a
   partner, a warehouse, lines with article, unit, quantity and unit price, and it never writes a ledger entry.
2. **Lifecycle: `draft -> confirmed <-> closed`.** A draft is freely edited and has no effect. Confirmation
   assigns the gapless number (`PO-000001`, `SO-000001`), freezes factor and base quantity of every line
   (ADR-0014, decision 4) and makes header and lines immutable. `close` ends fulfilment (the rest will not
   come); `reopen` undoes a close. There is no `cancelled` status: a confirmed order closed with nothing
   fulfilled is a cancelled order. **A confirmed order is never edited**; to change it, close it and create a
   new one.
3. **Fulfilment is a stock document linked to the order**: a `receipt` with `purchaseOrderId`, an `issue` with
   `salesOrderId`. It is created, edited, posted, numbered and reversed by the rules of specs 005–008; the
   link adds checks, never a second posting mechanism. The link is set at creation and cannot change.
4. **A fulfilment line names the order line (`orderLineNo`) and repeats its article.** The article must match
   the order line's (`409 ORDER_MISMATCH`). The same article may be on several order lines, so the line number
   is what links; the repeated article is the guard against an off-by-one line number.
5. **Progress is kept in base units.** Per order line: ordered (`baseQuantity`, frozen at confirmation),
   fulfilled (the sum of the base quantities of the linked lines on posted, not reversed, stock documents) and
   outstanding. A fulfilment line may use any unit of the article.
6. **Fulfilment never exceeds the order.** Posting is refused (`409 QUANTITY_EXCEEDS_ORDER`) if it would take
   an order line above its ordered quantity. There is no tolerance setting.
7. **The checks are made at posting, under a lock on the order** — as stock sufficiency is (ADR-0012). A draft
   fulfilment document reserves nothing of the order.
8. **Fulfilment happens in the order's warehouse** (`ORDER_MISMATCH` otherwise). That is what makes
   "incoming" and "reserved" quantities per (article, warehouse) meaningful.
9. **Only `stock` articles can be ordered** for now. Services on orders arrive with invoices (spec 014).
10. **A reversal of a fulfilment document** (ADR-0013) lowers the fulfilled quantity again. It is allowed
    whatever the order's status: a mistake must stay correctable after the order was closed.
11. **Prices are plain numbers in the tenant's one currency**, per unit of the line. `lineAmount` is
    quantity × unit price rounded to 2 decimal places, half away from zero; `totalAmount` is the sum of the
    line amounts. No tax, no discount, no currency code yet.
12. **The partner's role is checked when it is put on an order and when the order is confirmed**
    (`409 PARTNER_ROLE_MISSING`), like `isActive`. It is not frozen: a partner used by orders may lose the role.
13. **Open orders are visible in stock on hand**: `incomingQuantity` (outstanding on confirmed purchase orders)
    from spec 009; `reservedQuantity` and `availableQuantity` from spec 010 (ADR-0017).

## Alternatives
- **Separate `GoodsReceipt` / `Delivery` resources.** Their own tables would need their own posting,
  numbering and reversal, and a ledger entry pointing at one of several document tables — the reason ADR-0012
  and ADR-0015 chose one stock document. Rejected.
- **Link by article, one line per article on an order.** No line numbers on the fulfilment line, but an order
  could not carry the same article twice (two prices, free goods, two delivery dates). Rejected.
- **Link by line number only, article derived.** Shorter requests; a wrong line number then silently receives
  the wrong article. Rejected.
- **"Receive" as one action on the order that creates and posts.** One call for the common case, but a second
  way to post stock, without a draft to inspect. Rejected; an order-linked receipt is one create and one post.
- **Editable confirmed orders, or order versions.** What real orders need eventually (a changed quantity or
  price). It requires rules for every field against what was already fulfilled, and later invoiced. Deferred.
- **Over-receipt tolerance** (BC, SAP: percentage per item or partner). Deferred; an extra quantity is received
  with a manual receipt or a second order.
- **Automatic closing when fully fulfilled.** Then a reversal would have to reopen the order by itself, and
  invoicing (014) still needs the order. `receiptStatus` / `deliveryStatus` say "full" instead.
- **A `cancelled` status beside `closed`.** A distinction only reports would use. Rejected for now.
- **Track progress in the order line's unit.** Reads naturally ("3 of 5 boxes"), but a receipt in another
  unit or after a factor change has no exact value in it. Rejected.
- **Amounts unrounded, or decimals per currency.** Unrounded products have up to 12 decimals; a currency
  master with its own precision belongs to multi-currency (roadmap, "Later"). Two decimals is the default.

## Consequences
- Stock documents gain two optional header links and one line field; unlinked documents behave as before.
- An order line's progress is derived from posted stock documents; nothing else can change it.
- An order agreed wrongly is corrected by close + new order; the number series therefore contains closed
  orders.
- Valuation (011) takes the cost of a linked receipt from its order line: unit price ÷ the order line's
  factor, per base unit.
- Invoices (014) will be created from posted fulfilment documents and will add their own progress
  (invoiced quantity) to order lines.
- Partner, warehouse, article and unit of measure are `IN_USE` while an order names them.

## Amendment (2026-10-10): "never more than ordered" is a default (ADR-0020)
Over-receipt and over-delivery become tenant-configurable tolerances (`purchase.overReceiptPercent`,
`sales.overDeliveryPercent`, default 0; spec 012). Progress is still derived only from posted stock
documents; with a tolerance above 0 it may exceed the ordered quantity, and the outstanding quantity is then
0, never negative. The other choices of this ADR are classified in `docs/rules.md`.
