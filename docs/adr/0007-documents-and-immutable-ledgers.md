# ADR-0007: Documents post into append-only ledgers; corrections are reversals

Status: accepted (2026-10-08) — binds future inventory and accounting specs

## Context
Every system studied (see `docs/roadmap.md`) separates an editable business document from the permanent
entries it produces: Business Central (document -> posted document + item/customer/G/L ledger entries),
ERPNext (submitted document -> Stock Ledger Entry / GL Entry), Odoo (picking -> stock moves; invoice -> journal
items), SAP Business One (a posted journal entry "cannot be changed, only reversed"). With agents as writers,
a non-rewritable history is also the main safety property.

## Decision
- Stock on hand and account balances are never stored as editable fields; they are sums over ledger rows
  (cached projections are allowed, but the ledger is the truth).
- Transactional documents have a status lifecycle `draft -> posted -> (cancelled by a reversing document)`.
  Drafts are freely editable and have no ledger effect. Posting is one atomic transaction that validates,
  assigns the final document number, writes ledger rows and makes the document immutable.
- Ledger rows are insert-only; a correction is a new document with opposite rows referencing the original.
- Quantity and value are tracked separately (quantity ledger first, valuation later), as in Business Central's
  item ledger entry / value entry split.
- Stock movements use explicit document types (receipt, issue, transfer, adjustment) per warehouse, as in
  ERPNext and Business Central, rather than Odoo's uniform location-to-location moves with virtual
  partner/loss locations.

## Alternatives
- **Mutable balance columns updated in place.** Simple, unauditable, race-prone. Rejected.
- **Odoo's double-entry location model** (every move is from a location to a location; vendors, customers and
  inventory loss are virtual locations). Elegant and uniform, but it needs a location hierarchy before the first
  receipt can be recorded. Deferred; the ledger shape (signed quantity per item and warehouse) does not preclude it.
- **Full event sourcing.** The ledgers already are the events that matter; no need to event-source master data.

## Consequences
- "Edit a posted document" does not exist; UI and agents must use reverse-and-recreate.
- Posting needs concurrency control per item/warehouse (negative stock checks) and gapless numbering.
- Reports are sums over ledgers; projections/indexes will be needed as volume grows.
