# ADR-0012: Stock documents and the stock ledger — one document resource, exact quantities, no negative stock

Status: accepted (2026-10-09) — applies ADR-0007 to inventory; first applied by spec 005, continued by 006.
Points 4, 5, 6 and 8 are the architect's defaults and are listed for the owner in the hand-over of spec 005.
Amended 2026-10-10 by ADR-0018: the stored balance is now required (it supersedes the second consequence
below), and rebuild joins the writes of condition 1 of the amendment. Spec numbers past 010 in this ADR are
those of 2026-10-09 (`docs/roadmap.md`, renumbering of 2026-10-10).

## Context
ADR-0007 fixed the principle: editable documents post into an append-only ledger, and corrections are
reversals. Spec 005 builds the first such document. That needs decisions ADR-0007 left open: the shape of the
document, how quantities travel in JSON, how a posted document gets its number while configurable number
series are a later spec, and what "negative stock" means when documents carry dates.

## Decision
1. **One resource, `StockDocument`, with a `type`** (`receipt`, `issue`; `transfer` in spec 006), a header and
   1–200 lines. The type is chosen at creation and never changes. Status is `draft` or `posted` (`reversed`
   arrives with spec 006).
2. **The ledger entry is the unit of truth:** article, warehouse, signed quantity in the article's base unit,
   the document and line that produced it, the document date, and who posted it when. One posted line produces
   one entry (a transfer line, two). Entries are never updated or deleted. Stock on hand is, by definition, the
   sum of the entries of an (article, warehouse) pair.
3. **Posting is one transaction** that checks the document, assigns its number, writes the entries and makes
   the document immutable. It either does all of it or nothing — a failed posting consumes no number.
4. **Stock never goes negative.** A posting is refused (`409 INSUFFICIENT_STOCK`) if it would take the on-hand
   quantity of any (article, warehouse) pair below zero. There is no setting to allow it. Postings are
   serialised as far as needed to make this hold under concurrency.
5. **The check is against stock on hand now, not as of the document date.** `documentDate` is the business date
   the user assigns (any calendar date, past or future); it is stored on the entries for reporting and later
   for valuation and periods. The ledger is ordered by posting time.
6. **Document numbers: a fixed pattern per type, per tenant, gapless** — `SR-000001` (receipt), `SI-000001`
   (issue), assigned at posting from a counter that is locked inside the posting transaction. Drafts have no
   number. Spec 013 (number series) later makes the pattern configurable on top of the same counter.
7. **Quantities are exact decimals and travel as JSON numbers.** A line quantity is greater than 0, has at most
   6 decimal places and is at most 999,999,999.999999 — at most 15 significant digits, so the value survives a
   client that parses JSON numbers as IEEE doubles. Stored as `numeric`; never a floating-point type
   (architecture §8). Responses may write `10`, `10.0` or `10.000000`; consumers compare numerically.
8. **Lines are in the article's base unit.** A line shows the unit but cannot choose one until spec 007.
9. **Drafts hold references but reserve nothing.** A draft makes its warehouse and articles `IN_USE` (they
   cannot be deleted) and freezes the articles' `type` and base unit, exactly as a posted document does; it has
   no effect on stock on hand and reserves no quantity.
10. **Posting re-checks that the warehouse and every article are active.** An inactive master cannot be newly
    used (ADR-0008) and cannot be posted against, even from a draft made while it was active.
11. **Only `stock` articles can be on a stock document** (`409 ARTICLE_NOT_STOCKED`).
12. **Errors about a line are keyed by position:** `lines[0].quantity` — zero-based index into the request's
    `lines` array, then the field name. This is the convention for every document with lines.

## Alternatives
- **Separate resources per movement type** (`/stock-receipts`, `/stock-issues`, …). Simpler representation per
  type, but three near-identical sets of operations and tools, and a ledger entry that must point at one of
  several tables. ERPNext uses one Stock Entry with a purpose; chosen likewise.
- **Allow negative stock, or make it a tenant setting** (ERPNext has "Allow Negative Stock"; BC allows it by
  default with a warning). Convenient when paperwork lags the physical flow, but it makes every later
  valuation rule a special case, and an agent that issues stock that does not exist should be told, not
  obeyed. A setting can be added later without changing the ledger.
- **Check availability as of the document date** (no back-dated issue before the receipt that supplied it).
  Stricter and what valuation by date would like, but it rejects legitimate late paperwork and needs the whole
  date-ordered history re-checked on every back-dated posting. Deferred until valuation (011) and accounting
  periods (012) define what dates must obey.
- **Quantities as JSON strings.** Immune to float parsing at any precision, but unnatural for agents and for
  schemas (`"quantity": "10"`). The 15-significant-digit bound gives the same safety with numbers.
- **Number the document at creation.** Then deleted drafts leave gaps, or drafts cannot be deleted.
  Rejected (roadmap: drafts get no legal number).
- **A database sequence for numbers.** Not gapless: a rolled-back posting burns a value.
- **Reserve stock for drafts.** That is what sales orders will do (010), with their own rules; a draft stock
  document is only an unfinished entry form.
- **Snapshot article code/name/unit on the posted line.** The ledger stores ids; codes and names shown are the
  current ones (ADR-0008). Because the base unit is frozen once an article is used, the quantity's meaning
  cannot change; only labels can.

## Consequences
- Posting serialises per number counter and per (article, warehouse) pair: fine at the volume of a small
  company; a known limit for bulk imports. (For the MVP: per tenant — see the amendment below.)
- Stock on hand is a `SUM` over the ledger until volume requires a projection; the projection is then an
  implementation detail that must stay equal to the sum (a criterion in spec 005 states the equality).
- Opening balances are entered as receipts until the stock count spec (008) exists.
- A manual receipt or issue has no partner and no price. Goods received from a supplier with a price come from
  purchasing (009); value comes with valuation (011).
- A mistake in a posted document cannot be corrected until spec 006 (reversal) — except by a compensating
  document of the opposite type, which is itself a correct ledger operation.
- `IN_USE` now also means "this field cannot change while the record is used" (article `type`, `baseUnitId`).

## Amendment (2026-10-09): one lock per tenant is accepted for the MVP
Asked by the builder of spec 005 (`docs/questions/005-q.md`, B-Q2). Decision 4 says postings are serialised
"as far as needed"; the spec notes named the narrowest set of locks (document row, the affected
(article, warehouse) pairs in a fixed order, the counter row). The builder serialised wider: every write of a
stock document and the replace of an article run in one transaction that holds a `FOR NO KEY UPDATE` lock on
the tenant's row.

**Accepted, for the MVP (specs 001–010).** Correctness comes first, and a single lock gives it by
construction: no lock order to get wrong, no deadlock, one writer per counter, and the stock, order
quantities and frozen master fields a write decides on cannot change before it commits. Throughput inside one
tenant is secondary at the volume of a small company; other tenants and all reads are not affected, and
ordinary master writes are not blocked (their foreign-key checks take a key-share lock, which does not
conflict).

Conditions:
1. **Every write whose decision depends on shared state runs under the same lock**: create, replace, delete,
   post and reverse of stock documents (005, 006, 008); the replace of an article, changes of its unit
   conversions and the delete of an article, which deletes its conversions (005/R26, 007); and create, replace, delete, confirm, close and reopen of orders (009, 010).
   A write that takes the lock for only part of its decision is a defect.
2. **Nothing slow happens inside it**: no network call, no waiting on anything but the database.
3. **The contract does not depend on it.** No criterion may pass only because of the coarse lock, and none
   may be dropped because of it: the race criteria (005/AC-46, AC-52, AC-53 and their successors) stay.
   Narrowing the lock later is an implementation change with no migration and no change of the API.
4. Where a spec's builder note prescribes a lock order (006, 008, 009, 010), the per-tenant lock satisfies it.

Known limits, accepted: a bulk import of many documents for one tenant runs one write at a time; a
long-running posting (a document of 200 lines) delays that tenant's other stock and order writes for its
duration. Revisit when a tenant's write volume makes this visible, or with row-level security (019) if the
runtime role changes what can be locked.

## Amendment (2026-10-10): the rules of this ADR are defaults (ADR-0020)
"Negative stock is always refused", six decimals and the fixed number patterns are business rules, not
invariants: they become tenant-configurable rules with these values as defaults (`docs/rules.md`;
`stock.negativeStock` and `quantity.decimals` with spec 012). What stays absolute: the ledger is append-only,
a posted document is immutable, stock on hand is the sum of the ledger. A change of a rule takes the
per-tenant lock (condition 1).
