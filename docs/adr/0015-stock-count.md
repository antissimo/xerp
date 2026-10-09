# ADR-0015: A stock count is a stock document that posts the difference to the book quantity it was prepared against

Status: accepted (2026-10-09) — continues ADR-0012, ADR-0013 and ADR-0014; first applied by spec 008.
Points 2, 3, 4, 6 and 7 are the architect's defaults and are listed for the owner in the hand-over of spec 008.

## Context
Stock in the system drifts from stock on the shelf (loss, damage, miscounts), and a new tenant has to enter
what it already owns. Both are the same act: someone counts, and the system must make stock equal to the
count without anyone editing a quantity. Open questions: where the count lives, what exactly is posted, and
what happens when stock moves between counting and posting.

## Decision
1. **A count is a fourth type of `StockDocument`, `count`**, for one warehouse, with the same draft,
   posting, numbering (`SC-000001`), immutability, reversal and `IN_USE` rules as the other types
   (ADR-0012, decision 1).
2. **A count line states the counted quantity of one article**, zero allowed. Each article appears at most
   once on a count. Articles not listed are not touched: a count is partial, never "everything else is zero".
3. **Posting writes the difference**, counted minus book, as one ledger entry per line; a line without a
   difference writes none. After posting, stock on hand of every counted pair equals the counted quantity.
4. **The book quantity is recorded on the draft line when the draft is saved** (create and every replace), and
   **posting is refused (`409 COUNT_OUTDATED`) if stock on hand of a counted pair no longer equals it.** The
   difference shown on the draft is therefore exactly what posting writes. Saving the draft again takes the
   new book quantity.
5. **A count never fails for insufficient stock** — the counted quantity is not negative, so the result is not.
6. **A posted count is reversed like any stock document** (ADR-0013): the entries are negated, subject to "no
   negative stock". There is no other way to change it.
7. **Nothing is frozen while a count is open.** Receipts, issues and transfers keep posting; decision 4 is what
   protects the count.
8. **An adjustment by a known amount is a receipt or an issue** (spec 005). A count is for "I do not know the
   difference, I know what is there". Opening balances are a count on empty stock.

## Alternatives
- **A separate resource `/stock-counts`.** Line semantics do differ (counted quantity, zero allowed), but a
  separate table needs its own posting, numbering, reversal and a ledger entry that points at one of two
  document tables — the reason ADR-0012 chose one resource. Rejected.
- **Post the difference to whatever stock is at posting time, without the check** (ERPNext Stock
  Reconciliation: sets the quantity as of posting). Simplest, but a count taken in the morning and posted in
  the evening silently erases the day's movements; an agent would never learn it happened. Rejected.
- **The caller sends the expected quantity** and the server compares. Same protection, but every client must
  read stock first and can send a stale or invented number. Recording it at save does it once, on the server.
- **Freeze the warehouse or the counted articles while a draft count exists** (classic physical-inventory
  lock). Certain, but one forgotten draft blocks all movements. Deferred; decision 4 covers the risk.
- **Several lines per article, summed** (boxes on one line, loose pieces on another; two shelves). Natural
  on a count sheet, but then book quantity and difference belong to the article, not the line, and the ledger
  entry has no single line. The caller adds the quantities up. May return with bins/locations.
- **A full count: articles in stock but not listed are set to zero.** Dangerous by default; an explicit line
  with quantity 0 says the same on purpose.
- **Reason codes for differences** (loss, damage, found). `note` and `reference` carry it for now; reasons
  matter once differences are valued and posted to accounts (011, 012).

## Consequences
- A document line now has two more properties, `bookQuantity` and `differenceQuantity`, `null` on other types.
- A count draft goes stale by design; the remedy (`PUT` again) is cheap and is said in the tool description.
- A count that finds everything in order is still a numbered, posted document — proof that the count happened.
- Reversing a count restores the previous book quantity, not "the truth"; the correction is then a new count.
- Valuation (011) must give a cost to positive count differences, which have no purchase price.
