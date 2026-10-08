# ADR-0013: Stock transfers are one posting; a reversal is a linked reversing document

Status: accepted (2026-10-09) — continues ADR-0007 and ADR-0012; first applied by spec 006.
Points 1, 4, 5, 6 and 7 are the architect's defaults and are listed for the owner in the hand-over of spec 006.

## Context
Spec 005 left two things open: moving stock between warehouses, and correcting a posted document. ADR-0007
already says corrections are reversals; it does not say what a reversal is in data.

## Decision
1. **A transfer is one document and one posting.** Type `transfer` has a source (`warehouseId`) and a
   destination (`toWarehouseId`); each line writes `−quantity` in the source and `+quantity` in the
   destination in the same transaction. There is no in-transit state.
2. **Transfers have their own gapless number series** per tenant, `ST-000001`, by the rules of ADR-0012.
3. **A reversal creates a reversing document**, already posted, of the same type, with the same warehouses and
   lines, linked to the original (`reversalOf` / `reversedBy`). Its ledger entries are the original's with the
   opposite sign. The original's status becomes `reversed`; nothing else on it changes.
4. **The reversing document takes the next number of the same type's series.** A reversed receipt `SR-000001`
   is reversed by, say, `SR-000002`. No separate series for reversals.
5. **A reversal is whole and final.** It cancels the entire document; a reversing document cannot be reversed;
   a reversed document cannot be reversed again. A correction is: reverse, then create a new document.
6. **A reversal obeys "no negative stock"** (ADR-0012, decision 4). A receipt whose goods have since been issued
   cannot be reversed until the later movements are reversed or stock is replenished.
7. **The caller gives the reversal's date**, which may not be earlier than the original's date. The server has
   no notion of the tenant's "today" yet (no time zone), and the date is a business fact that valuation and
   periods will care about.
8. **A reversal does not require active masters**, unlike a posting (ADR-0012, decision 10): deactivating an
   article must not make an earlier mistake uncorrectable.

## Alternatives
- **Two documents for a transfer** (issue at the source, receipt at the destination, linked), or an
  **in-transit** warehouse (BC transfer orders, Odoo transit locations). Models goods on the road, but allows
  the half-done state this spec exists to exclude, and needs rules for receiving less than was sent. Deferred
  until someone needs transit; it can be added as a further type without changing this one.
- **Reversal as a status change only** (ERPNext "cancel": the document becomes cancelled and reversing ledger
  entries are attached to it). One document fewer, but the reversal then has no number, date, author or note of
  its own, and the ledger entry's document no longer tells what happened. Rejected.
- **A separate number series for reversals** (`SX-…`). Makes a reversal recognisable by its number, but adds a
  counter per type and a fourth pattern for spec 013 to configure. The `reversalOf` link and `isReversal` on
  ledger entries already say it. Rejected for now.
- **Reversal as a document of the opposite type** (reverse a receipt with an issue). Reuses everything, but the
  history then shows an issue that never physically happened, and a transfer's opposite is a transfer with
  swapped warehouses, which hides the link. Rejected.
- **Allow reversal to drive stock negative.** Would make corrections always possible, at the price of the one
  invariant every later spec relies on. Rejected.
- **Reversal dated automatically** (original's date, or the server's date). The original's date rewrites the
  past for valuation; the server's date depends on a time zone the tenant has not configured. Rejected.
- **Partial reversal / line reversal.** Belongs to returns and to purchasing and sales documents (009, 010),
  which have quantities to match against.

## Consequences
- A reversed pair stays in every list and in the ledger forever; reports that want "effective" movements
  filter on `status` and `isReversal`.
- Number series contain reversing documents; "how many receipts were posted" is not `LastNumber`.
- The only permitted change to a posted document is `posted -> reversed` with the link to its reversal.
- Posting a transfer locks pairs in two warehouses; all postings must lock pairs in one fixed order.
- Later documents that create stock movements (goods receipt 009, delivery 010, stock count 008) reuse the
  same reversal mechanism.
