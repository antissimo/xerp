# ADR-0018: The stored stock balance — a projection of the ledger, written with it, never trusted over it

Status: accepted (2026-10-10) — owner request for spec 011; supersedes the second consequence of ADR-0012
("stock on hand is a `SUM` over the ledger until volume requires a projection"). Points 4, 6 and 7 are the
architect's defaults and are listed for the owner in spec 011 §12.

## Context
Through the MVP, stock on hand was computed: the sum of the append-only ledger per (article, warehouse)
(ADR-0012, decision 2). The owner now asks that the quantity of every article in every warehouse be **stored
in the database** — it is what a stock list shows and what a person expects to find in a table — while the
documents stay the source of truth: the stored number must be derivable from them ("event sourcing").

The system already has the event log. Posted documents are the events' origin; posting turns each into
immutable ledger entries; nothing else changes stock. What is missing is the materialised state and the
rules that keep it honest.

## Decision
1. **A table `StockBalance`, keyed by (tenant, warehouse, article), holds one number: the quantity in the
   article's base unit.** It is tenant-owned like every table. It has no history, no date and no audit
   columns: it is a sum, not a record of an act.
2. **The ledger stays the truth.** The stored balance of a pair is, by definition, the sum of the pair's
   ledger entries. If the two ever differ, the ledger is right and the balance is wrong — by definition, with
   no case-by-case judgement.
3. **The balance is written where the ledger is written, and only there:** in the same transaction and under
   the same per-tenant lock (ADR-0012 amendment) as the entries of a posting or a reversal, each entry adding
   its signed quantity to its pair. A posting commits its document, number, entries and balances together or
   not at all. No endpoint, tool, draft, order or master change writes a balance; no API accepts a quantity
   to store.
4. **Readers use the stored balance.** Stock on hand, the stock list, the no-negative-stock check, the book
   quantity and "current" check of a count all read `StockBalance`. A stored number nobody reads would rot
   unnoticed; one that every decision reads is exercised by every test of specs 005–011.
5. **The invariant is a contract, not an implementation detail:** for every pair, stored == sum of ledger,
   after every committed operation and under parallel postings; never negative. Spec 011 states it as
   acceptance criteria, including races.
6. **Two operations make the derivation real.** *Verify* lists the pairs whose stored balance differs from
   the ledger sum, both read as of one moment. *Rebuild* replaces a tenant's balances by the ledger sums,
   under the tenant lock, and says how many pairs it corrected. In a correct system verify is always empty
   and rebuild corrects nothing; they exist so that this can be shown at any time, and so that a defect or a
   manual intervention in the database can be found and repaired without touching the ledger.
7. **Nothing repairs itself silently.** A difference is reported (and logged) by verify; only an explicit
   rebuild changes balances. A posting does not re-derive the pairs it touches.
8. **The database is the second barrier:** `Quantity >= 0` as a check constraint, so "no negative stock"
   (ADR-0012, decision 4) holds even against a defect in the application.
9. **The migration fills the table from the ledger** for all existing tenants; it is the first rebuild.

## Alternatives
- **Keep computing the sum** (the MVP). Always right by construction and one table fewer. Rejected by the
  owner's request; it also makes a dense stock list (every article, zeros included) and every sufficiency
  check a scan of the pair's whole history, which grows without bound.
- **A database view or materialised view over the ledger.** A plain view is the computed sum under another
  name — nothing is stored. A materialised view is stored but refreshed as a whole and outside the posting
  transaction: between refreshes it is stale, so the no-negative check could not read it.
- **A trigger on the ledger table that maintains the balance.** Atomic and impossible to forget, but it puts
  a business invariant in the database where the layer rule (architecture §2) and the tests do not look, and
  it would run for the migration and rebuild too. One writer in Application, reviewed as such (011/AC-05),
  plus verify, gives the same guarantee where the rest of the rules live.
- **The balance as the truth, the ledger as its log** (the classic "quantity on hand" column that documents
  update). Then a wrong balance cannot even be detected, and reversal, counts and valuation would have to
  trust it. Contradicts ADR-0007 and the owner's own condition.
- **Asynchronous projection** (an outbox or event stream consumed after commit — event sourcing in the
  narrow sense). Scales writes, but the balance lags the ledger, so the sufficiency check must read the
  ledger anyway or accept overselling. Nothing in a single-database system at this volume needs it.
- **Rebuild by replaying documents** instead of summing entries. The ledger entries *are* the replayed
  documents, written once at posting and immutable; replaying documents again would be a second
  implementation of posting that could disagree with the first.
- **Automatic repair when verify finds a difference.** Hides the defect that caused it. The difference is
  the finding; repairing is a decision.
- **Rows for every (article, warehouse) pair, zeros included,** so that the stock list is a plain select.
  Then creating an article or a warehouse writes balances — a second writer — and the table grows as
  articles × warehouses. No row means zero; the stock list starts from the articles instead.

## Consequences
- Every posting does one more write per affected pair, inside a transaction that already locks the tenant.
  Reads of stock no longer depend on the length of the history.
- "Stock on hand is the sum of the ledger" (005/R19) stays true as an invariant and stops being the way the
  number is obtained. No result of specs 005–010 changes.
- A new code path that writes ledger entries without the balance is the one way to break the invariant; the
  single writer (011/AC-05), the race criteria and verify are the guards. Every later spec that posts to the
  stock ledger (returns, production) inherits the rule: entries and balances together.
- The per-tenant lock now also orders rebuild against postings (ADR-0012 amendment, condition 1 extended).
  Narrowing that lock later must keep the pair's balance row inside whatever guards the pair.
- Valuation (spec 012) will want value next to quantity. Whether value is stored the same way is decided
  there; this ADR is the pattern: a ledger that is the truth, a stored projection written with it, verify and
  rebuild.
- Stock as of a date, and any history of balances, still come from the ledger.

## Amendment (2026-10-10): the balance may be negative where the tenant allows it (ADR-0020)
With `stock.negativeStock` = `allow` a balance can be below zero, so the check constraint `Quantity >= 0`
(decision 2) is removed by spec 012. The invariant of this ADR is untouched: stored balance == sum of the
ledger; verify and rebuild work for any sign.
