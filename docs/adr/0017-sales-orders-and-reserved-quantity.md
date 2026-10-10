# ADR-0017: Sales orders mirror purchase orders; reserved quantity is information, not a lock

Status: accepted (2026-10-09) — continues ADR-0012 and ADR-0016; first applied by spec 010.
Points 2, 3, 4 and 5 are the architect's defaults and are listed for the owner in the hand-over of spec 010.

## Context
ADR-0016 fixed what an order is and how fulfilment links to it. A sales order adds one question a purchase
order does not have: the goods it promises come out of stock that other documents can also take. The roadmap
asks for a "reserved (committed) quantity". It also lists an "optional quotation".

## Decision
1. **A sales order is ADR-0016 applied to the outbound side**: `SalesOrder` with a customer, the warehouse it
   ships from, the same lifecycle, numbering (`SO-000001`), immutability, prices and amounts. A **delivery** is
   an `issue` stock document linked with `salesOrderId` and `orderLineNo`; posting it is refused above the
   ordered quantity (`QUANTITY_EXCEEDS_ORDER`) and, as any issue, above stock on hand (`INSUFFICIENT_STOCK`).
2. **Reserved quantity is derived, per (article, warehouse)**: the sum of the outstanding base quantities of
   the lines of `confirmed` sales orders shipping from that warehouse. Nothing stores or edits it.
3. **Available quantity = on hand − reserved, and may be negative.** A negative value says: more is promised
   than is there.
4. **Reservation blocks nothing.** Confirming an order is never refused for stock (selling before buying is
   ordinary trade). Sufficiency at posting is, as before, against stock on hand: an unlinked issue or a
   transfer may take goods a sales order was counting on, and of two orders for the same goods the first
   delivery posted is served. `availableQuantity` is how a caller sees it coming.
5. **There is no quotation document.** A draft sales order is the offer: it has prices and a total, commits
   nothing and reserves nothing; confirming it is accepting it.
6. **Reversing a delivery brings the goods back** (ADR-0013) and lowers the delivered quantity; while the
   order is confirmed the quantity is reserved again. A partial return is not modelled yet.

## Alternatives
- **Hard reservation: allocate stock to an order line at confirmation; other documents cannot take it**
  (Odoo's reserved quants, BC item tracking / reservation entries). Guarantees the promise, at the price of a
  second stock invariant: confirmation fails or splits when stock is short, allocations must be released,
  moved and re-made by counts, transfers and reversals, and back-orders need a separate path. Deferred until a
  tenant needs the guarantee; the derived figure does not preclude it.
- **Refuse unlinked issues and transfers that would take stock below the reserved quantity.** A lock without
  allocation: once orders exceed stock every unlinked movement of the article is blocked, including the
  transfer that would bring goods where they are needed. Rejected.
- **Check availability at confirmation and answer with a warning.** The contract has no warnings; an agent
  reads `availableQuantity` before or after confirming.
- **A quotation resource with "convert to order".** A second document with the same lines and no effect that
  a draft does not already have. What a real quotation adds — validity date, versions, a number to quote, a
  "lost" outcome — is a sales-process feature, not inventory. Deferred.
- **Reserve from drafts.** Then offers would empty the shelf. Rejected: a draft commits nothing.
- **Include incoming purchase quantities in "available".** Goods not yet here cannot be delivered; a
  projected figure by date needs dates the documents do not reliably carry yet.

## Consequences
- Stock on hand now answers three questions per (article, warehouse): what is there (`quantity`), what is
  coming (`incomingQuantity`), what is promised (`reservedQuantity`), and `availableQuantity`.
- A delivery against an order leaves `availableQuantity` unchanged: stock and reservation fall together.
- An agent can confirm more orders than stock covers. That is intended and visible, not prevented.
- Permissions (017) may later separate "may confirm sales orders" from "may post deliveries".
- Invoices (014) will be created from posted deliveries; cost of goods sold needs valuation (011).

## Amendment (2026-10-10): "informs, does not block" is a default (ADR-0020)
`sales.reservedStockProtected` (default `false`; spec 012) lets a tenant make reserved stock
untouchable by other movements. The definitions of reserved and available quantity are unchanged.
