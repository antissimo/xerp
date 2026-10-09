# Spec 006 — Stock transfers and reversal

Status: ready for the tester and the builder once spec 005 is merged to `main`.
Branches: `tests/006-stock-transfers-reversal` (tester), `feat/006-stock-transfers-reversal` (builder).
Read first: **spec 005** (this spec extends it and repeats none of it), ADR-0007, ADR-0012, **ADR-0013**.
Why this is the sixth spec: `docs/roadmap.md` section 3 — second half of the stock core.

**Inherited, not re-specified** (roadmap §4): everything specs 001–005 established — transport, errors,
references, MCP mapping, and all of spec 005's rules for stock documents, posting, numbering, quantities,
stock on hand and the ledger. This spec states only what is new or changed. Numbers are local; "005/R15" means
rule R15 of spec 005.

## 1. Goal

Complete the stock core with the two things spec 005 left out:

- A **transfer** moves goods from one warehouse to another in one posting: the stock leaves the source and
  arrives at the destination together or not at all, and the company's total stock does not change.
- A **reversal** is the only way to correct a posted document: it posts a linked reversing document whose
  ledger entries cancel the original's exactly. Nothing is edited or deleted; the history shows both.

## 2. Scope

In scope
1. Document type `transfer` with a destination warehouse; its posting.
2. Operation **reverse** for posted receipts, issues and transfers; status `reversed`; the link between a
   document and its reversal.
3. Additions to the spec 005 representations and filters that these need.

Out of scope
- Goods in transit (a transfer that leaves on one day and arrives on another), partial reversal, reversal of
  single lines, "amend" (copy a reversed document into a new draft).
- Everything out of scope in spec 005.

## 3. Data

`StockDocument` gains: `ToWarehouseId` (null except for transfers), `ReversalOfId` (null except on a reversing
document), `ReversedById` (null until reversed). `Status` gains the value `reversed`.
`StockLedgerEntry` is unchanged in shape; a transfer line and a reversal produce entries as R6 and R14 say.
`DocumentCounter` gets a third type, `transfer`.

Keys: the three new columns are foreign keys that include `TenantId` and restrict deletes, like the others.
At most one reversing document per original, enforced by the database (unique `(TenantId, ReversalOfId)` where
not null). One migration.

## 4. Operations — HTTP

### 4.1 Changes to the stock document

Representation: three properties are added, on every stock document, always present:
```json
{ "…": "as spec 005 §4.1",
  "toWarehouse": { "id": "uuid", "code": "WH-2", "name": "Shop" },
  "reversalOf": null,
  "reversedBy": { "id": "uuid", "number": "ST-000002" } }
```
`toWarehouse` is `null` unless `type == "transfer"`. `reversalOf` is `{ id, number }` of the original on a
reversing document, otherwise `null`. `reversedBy` is `{ id, number }` of the reversing document on a reversed
document, otherwise `null`. The list summary carries the same three properties.

| Operation | Change |
|---|---|
| Create | `type` may be `"transfer"`. Body gains `"toWarehouseId"`: required for a transfer; for a receipt or issue it must be omitted or `null`. |
| Replace | Body gains `"toWarehouseId"`: must be present for a transfer; for a receipt or issue it may be omitted or `null`. |
| List | `type=transfer` and `status=reversed` are valid filter values. `warehouseId=` matches a document whose source **or** destination is that warehouse. |
| Post | Works for transfers (R5–R8). Additional error keys: `toWarehouseId`. |

### 4.2 Reverse

`POST /stock-documents/{id}/reverse` body `{ "documentDate": "YYYY-MM-DD", "note"?: string|null }`
-> `201` with the **reversing** StockDocument and `Location: /api/v1/stock-documents/{its id}`.
Errors: `400`, `404`, `409 INVALID_STATE`, `409 INSUFFICIENT_STOCK`.

### 4.3 Ledger entry

The `document` summary of a ledger entry gains `"isReversal": bool` — `true` when the entry was written by a
reversing document. `document.type` may now be `"transfer"`.

No new error codes.

## 5. Operations — MCP

| Tool | Change |
|---|---|
| `stock_document_create` | `type` allows `"transfer"`; new optional argument `toWarehouseId` (uuid or null). |
| `stock_document_update` | new optional argument `toWarehouseId` (uuid or null); required by rule for a transfer. |
| `stock_document_list` | `type` allows `"transfer"`, `status` allows `"reversed"`. |
| **`stock_document_reverse`** (new) | `{ id, documentDate, note? }` -> the reversing StockDocument. Errors: `VALIDATION_FAILED`, `NOT_FOUND`, `INVALID_STATE`, `INSUFFICIENT_STOCK`. |

- `stock_document_reverse` annotations: `readOnlyHint: false`, `destructiveHint: true`, `idempotentHint: false`,
  `openWorldHint: false`. Its description says: a reversal is permanent and cannot itself be reversed; it
  cancels the whole document; to correct a mistake, reverse and then create a new document; on
  `INSUFFICIENT_STOCK` the goods the original brought in have already left — reverse the later documents first.
- The descriptions of `stock_document_create` / `_update` explain `toWarehouseId` (destination of a transfer,
  different from `warehouseId`, which is the source).
- `tools/list` returns exactly 33 tools: the 32 of spec 005 and `stock_document_reverse`.

## 6. Business rules

Transfer — the document
- R1. `type` `"transfer"` moves goods from `warehouseId` (source) to `toWarehouseId` (destination).
- R2. For a transfer `toWarehouseId` is required and follows the reference rules exactly as `warehouseId`
  (005/R3), with `errors` key `toWarehouseId`. It must differ from `warehouseId`; equal -> `400` with key
  `toWarehouseId`.
- R3. For a receipt or issue a non-null `toWarehouseId` -> `400` with key `toWarehouseId`.
- R4. Lines, quantities, dates, `reference`, `note`, the draft lifecycle and the order of checks are those of
  spec 005; the destination reference is checked right after the source warehouse (005/R8).

Transfer — posting
- R5. Order of checks as 005/R13, with the destination warehouse among the masters that must be active
  (`REFERENCE_INACTIVE`, key `toWarehouseId`). Both warehouses are header masters: if both are inactive, one
  error carries both keys `warehouseId` and `toWarehouseId` (`006-q.md`, T-Q3).
- R6. Each line writes **two** ledger entries with the line's `lineNo`: `−quantity` in the source warehouse and
  `+quantity` in the destination warehouse. Both carry the document's date and the same `postedAt` /
  `postedBy`. In the ledger order (005 §4.3) the outgoing entry of a line precedes its incoming entry.
- R7. Sufficiency is that of an issue from the source warehouse (005/R15, R16): per article, the sum of the
  line quantities must not exceed stock on hand in the source. Stock in the destination does not count.
- R8. **Conservation.** A transfer changes no article's total over all warehouses: the entries of a posted
  transfer sum to zero per article. It is atomic — there is no moment and no failure after which stock has left
  the source without arriving at the destination.
- R9. Transfers are numbered `ST-000001`, … from their own counter per tenant, by the rules of 005/R17.
- R10. Both warehouses of a transfer are **used** by it (005/R24–R27), draft or posted.

Reversal
- R11. Only a document with status `posted` that is not itself a reversing document can be reversed. A draft,
  a `reversed` document and a reversing document -> `409 INVALID_STATE`; nothing changes.
- R12. `documentDate` of the reversal is required (a real calendar date, 005/R2) and must not be earlier than
  the original's `documentDate`; earlier -> `400` with key `documentDate`. `note` is optional (005/R7).
  Order of checks: validation of form -> `404` -> `INVALID_STATE` -> date not earlier than the original ->
  stock.
- R13. A reversal is atomic and does all of the following or nothing:
  - creates the **reversing document**: same `type`, warehouse(s) and lines (articles, quantities, order) as
    the original; `reference` copied from the original; `documentDate` and `note` from the request;
    `status == "posted"`; `reversalOf` = the original; `createdBy` = `postedBy` = the acting key; its own
    number, the next of the same type's counter (005/R17 — the series stays gapless);
  - writes its ledger entries (R14);
  - sets the original's `status` to `reversed` and its `reversedBy` to the reversing document. Nothing else
    on the original changes — not its lines, number, dates, `postedAt`, `postedBy` or `updatedAt`.
- R14. The reversing document's ledger entries are the original's with the opposite sign: for every entry of
  the original one entry with the same article, warehouse and `lineNo` and `−quantity`; `documentDate` is the
  reversal's date; `postedAt` / `postedBy` are the reversal's. The original's entries are untouched.
- R15. **Net zero.** After a reversal, the original's and the reversing document's entries sum to zero for
  every (article, warehouse) pair: stock is what it would be had the original never been posted.
- R16. **No negative stock, also by reversal.** A reversal is refused with `409 INSUFFICIENT_STOCK` if it would
  take any pair below zero — reversing a receipt needs the received quantity still on hand in that warehouse;
  reversing a transfer needs it on hand in the destination; reversing an issue always passes. `errors` keys are
  `lines[i].quantity` for the original's lines of each short article (as 005/R15). The original stays `posted`.
- R17. A reversal does **not** require the warehouse(s) and articles to be active: a mistake must stay
  correctable after a master was deactivated.
- R18. A reversing document is immutable like any posted document (005/R11) and can never be reversed (R11).
  A reversed document is immutable too. To redo the movement, create a new document.
- R19. Concurrency: of several simultaneous reversals of one document exactly one succeeds, the rest get
  `INVALID_STATE`; a reversal racing with postings that need the same stock obeys 005/R16.
- R20. Reversed and reversing documents keep their masters used for good (005/R27).

Reading
- R21. `status` values are `draft`, `posted`, `reversed`. A reversing document's status is `posted`; it is
  recognised by `reversalOf != null`.
- R22. Stock on hand is still exactly the sum of the ledger (005/R19); nothing in this spec changes stock except
  through ledger entries.

## 7. Edge cases

- E1. Transfer with `toWarehouseId` missing, `null`, `"abc"`, or equal to `warehouseId` -> `400` with key
  `toWarehouseId`; random UUID -> `409 REFERENCE_NOT_FOUND`; inactive -> `409 REFERENCE_INACTIVE`.
- E2. Receipt with `toWarehouseId` set -> `400`; with `"toWarehouseId": null` -> accepted.
- E3. `PUT` on a draft transfer may change source, destination and lines; it cannot change `type`.
- E4. Transfer of exactly the source's stock -> posted; the source pair disappears from stock on hand.
- E5. Two transfers in opposite directions posted in parallel -> both succeed if each is covered; never a
  deadlock surfaced as `500`.
- E6. Reversing a receipt of 100 after 40 were issued -> `INSUFFICIENT_STOCK`. After the issue is reversed,
  the receipt can be reversed.
- E7. Reversing a transfer W1 -> W2 after the goods were issued from W2 -> `INSUFFICIENT_STOCK`, although W1
  has plenty.
- E8. Reverse with `documentDate` one day before the original's -> `400`; equal to it -> accepted.
- E9. Reverse without a body, with `{}`, or with an unknown property (`"lines"`) -> `400`.
- E10. A reversing document: `PUT`, `DELETE`, `post`, `reverse` -> `INVALID_STATE`. The reversed original:
  the same.
- E11. Reversal of a document whose article was deactivated, renamed or re-coded -> succeeds; summaries show
  the current code and name.

## 8. Tenant isolation

- T1. Another tenant's warehouse as `toWarehouseId` -> `REFERENCE_NOT_FOUND`, as a random id.
- T2. Another tenant's document cannot be reversed: `404 NOT_FOUND`; it stays `posted` and its stock unchanged.
- T3. The transfer counter is per tenant. Foreign keys of the new columns include `TenantId`; no
  `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Any tenant key may post a transfer and reverse a document (as 005/S2; restrictable from spec 017).
  The reversing document's `postedBy` attributes the reversal to one key.
- S2. A reversal never updates or deletes a ledger entry. The only change to a posted document the application
  permits is the transition `posted -> reversed` with `reversedBy` (005/S3 is extended by exactly this).

## 10. Acceptance criteria

Conventions and "the standard setup", "receive n of A into W1" and "Stock(A, W1)" as in spec 005 §10.
"Transfer n of A from W1 to W2" means: create a transfer with one line and post it. Unmarked criteria are
black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The only earlier tests changed are
  the literal tool list, tests pinning the table/column list, tests asserting the exact property set of a stock
  document or ledger entry, tests pinning the input schema of the stock document tools (the new property
  `toWarehouseId`, the `type` and `status` enum values), and the spec 005 tests that expected `type`
  `"transfer"` to be invalid (005/AC-26, AC-63). One migration added.
- AC-02 *(builder)* Through the DbContext, a posted document accepts no modification other than
  `posted -> reversed` with `reversedBy`; a ledger entry accepts none (S2).
- AC-03 *(builder, unit)* The sign rules — entries of a transfer line (R6) and of a reversal (R14) — are
  unit-tested without HTTP.

Inherited behaviour — smoke
- AC-10 `POST /stock-documents/{uuid}/reverse` without a credential -> `401`; with the admin key -> `403`;
  with `?x=1` -> `400` with key `x`; with body property `"lines"` -> `400` with key `lines` (001/E4).

Transfer — draft
- AC-20 `POST /stock-documents` `{ "type": "transfer", "documentDate": "2026-10-09", "warehouseId": W1,
  "toWarehouseId": W2, "lines": [ { "articleId": A, "quantity": 30 } ] }` -> `201`; `type == "transfer"`,
  `status == "draft"`, `warehouse.id == W1`, `toWarehouse.id == W2` with W2's code and name,
  `reversalOf == null`, `reversedBy == null`. Stock and ledger are unchanged.
- AC-21 A receipt and an issue created as in spec 005 have `toWarehouse == null`, `reversalOf == null`,
  `reversedBy == null` (properties present).
- AC-22 Transfer validation: `toWarehouseId` missing, `null`, `"abc"` or equal to `warehouseId` -> `400` with
  key `toWarehouseId`; random UUID -> `409` `REFERENCE_NOT_FOUND` with key `toWarehouseId`; inactive warehouse
  -> `409` `REFERENCE_INACTIVE` with key `toWarehouseId`. A receipt with `"toWarehouseId": W2` -> `400` with
  key `toWarehouseId`; with `"toWarehouseId": null` -> `201`.
- AC-23 `PUT` on a draft transfer swapping source and destination and changing the lines -> `200` with the new
  values. `PUT` without `toWarehouseId` -> `400` with key `toWarehouseId`; with `toWarehouseId` equal to
  `warehouseId` -> `400`. `PUT` on a draft receipt without `toWarehouseId` -> `200`.

Transfer — posting
- AC-30 With 100 of A in W1: transfer 30 of A from W1 to W2 -> `200`, `status == "posted"`,
  `number == "ST-000001"`. Stock(A, W1) is 70, Stock(A, W2) is 30.
- AC-31 The ledger for that document (`documentId=`) has exactly two entries, both `lineNo == 1`,
  `document.type == "transfer"`, `document.isReversal == false`, same `postedAt`: first `quantity == -30` with
  `warehouse.id == W1`, then `quantity == 30` with `warehouse.id == W2`.
- AC-32 A transfer with lines (A, 10), (B, 5), (A, 2.5) with enough stock -> six entries; per article the
  entries of the document sum to 0; Stock(A, W1) + Stock(A, W2) and Stock(B, W1) + Stock(B, W2) are the same
  before and after.
- AC-33 No negative stock: with 70 of A in W1, a transfer of 71 -> `409` `INSUFFICIENT_STOCK` with key
  `lines[0].quantity`; the document is still a draft with `number == null`; Stock(A, W1) is 70 and Stock(A, W2)
  unchanged; no entry was written. A transfer with lines (A, 40), (A, 40) -> `INSUFFICIENT_STOCK` with both
  line keys.
- AC-34 Stock in the destination does not count: with 0 of A in W1 and 50 in W2, a transfer of 1 from W1 to W2
  -> `INSUFFICIENT_STOCK`.
- AC-35 A transfer of exactly the source's stock posts; the source pair is no longer in `GET /stock-on-hand`.
- AC-36 Posting needs both warehouses active: destination deactivated after the draft was saved -> `409`
  `REFERENCE_INACTIVE` with key `toWarehouseId`; the draft stays a draft; after reactivation posting succeeds.
- AC-37 Numbering: transfers get `ST-000001`, `ST-000002` independently of `SR-` and `SI-` numbers; a failed
  transfer posting consumes no number.
- AC-38 Race, same source: with exactly 10 of A in W1 and ten draft transfers of 3 from W1 to W2, all posted
  in parallel -> exactly three `200` and seven `409 INSUFFICIENT_STOCK`; Stock(A, W1) is 1, Stock(A, W2) is 9;
  numbers `ST-000001`…`ST-000003`.
- AC-39 Race, opposite directions: with 10 of A in W1 and 10 in W2, five draft transfers of 2 from W1 to W2
  and five of 2 from W2 to W1, all posted in parallel -> ten `200`, no other status; Stock(A, W1) is 10 and
  Stock(A, W2) is 10.

Transfer — masters and lists
- AC-40 A warehouse that is only the destination of a draft transfer: `DELETE /warehouses/{id}` -> `409`
  `IN_USE`; after the draft is deleted -> `204`.
- AC-41 `GET /stock-documents?type=transfer` returns only transfers; `warehouseId=W2` returns the transfers
  into W2 as well as documents on W2; each summary has `toWarehouse`.

Reversal — receipt and issue
- AC-50 Receive 100 of A into W1 (`SR-000001`), dated `2026-10-09`. `POST /{id}/reverse`
  `{ "documentDate": "2026-10-10", "note": "wrong article" }` -> `201`; the body is a new document:
  different `id`, `type == "receipt"`, `status == "posted"`, `number == "SR-000002"`,
  `documentDate == "2026-10-10"`, `note == "wrong article"`, `warehouse.id == W1`, one line (A, 100),
  `reversalOf.id ==` the original's id and `reversalOf.number == "SR-000001"`, `reversedBy == null`,
  `createdBy == postedBy ==` the acting key; `Location` ends with its id.
- AC-51 After AC-50: `GET` of the original -> `status == "reversed"`, `reversedBy.id ==` the reversing
  document's id, `reversedBy.number == "SR-000002"`; its `number`, lines, `documentDate`, `postedAt`,
  `postedBy` and `updatedAt` are unchanged. Stock(A, W1) is 0 and the pair is not listed.
- AC-52 After AC-50 the ledger for A in W1 has exactly two entries: `+100` (`document.number == "SR-000001"`,
  `isReversal == false`, `documentDate == "2026-10-09"`) and `-100` (`document.number == "SR-000002"`,
  `document.type == "receipt"`, `isReversal == true`, `documentDate == "2026-10-10"`, `lineNo == 1`,
  `postedBy ==` the reversing key).
- AC-53 With 100 of A in W1, an issue of 40 is posted and then reversed -> `201`, number `SI-000002`; the
  reversing entry is `+40`; Stock(A, W1) is 100 again.
- AC-54 Reversal by another key: document posted by K1, reversed by K2 -> the original's `postedBy == K1`; the
  reversing document's `createdBy == postedBy == K2`.
- AC-55 `reference` of the original is copied to the reversing document; `note` omitted -> `null`.

Reversal — transfer
- AC-56 After transferring 30 of A from W1 to W2 (W1 had 100): reverse -> `201`, `type == "transfer"`,
  `number == "ST-000002"`, `warehouse.id == W1`, `toWarehouse.id == W2`; its two entries are `+30` in W1 and
  `-30` in W2; Stock(A, W1) is 100, Stock(A, W2) is 0.

Reversal — rules
- AC-60 No negative stock by reversal: receive 100 of A into W1, issue 40; reversing the receipt -> `409`
  `INSUFFICIENT_STOCK` with key `lines[0].quantity`; the receipt is still `posted` with `reversedBy == null`;
  Stock(A, W1) is 60; no document and no entry was created; no number was consumed (the next posted receipt is
  `SR-000002`). After the issue is reversed, reversing the receipt succeeds and Stock(A, W1) is 0.
- AC-61 Transfer 30 of A from W1 to W2, then issue 10 from W2: reversing the transfer -> `INSUFFICIENT_STOCK`
  although W1 holds 70.
- AC-62 What cannot be reversed -> `409` `INVALID_STATE`, nothing changed: a draft; a reversed document
  (second reversal); a reversing document.
- AC-63 Reversed and reversing documents are immutable: `PUT` (valid body), `DELETE` and `POST /post` on each
  -> `409` `INVALID_STATE`.
- AC-64 Date rule: original dated `2026-10-09`; reverse with `documentDate` `"2026-10-08"` -> `400` with key
  `documentDate`, the original is still `posted`; with `"2026-10-09"` -> `201`. Reverse with no body, `{}`,
  or `documentDate` `"2026-02-30"` -> `400` with key `documentDate`.
- AC-65 Inactive masters do not block a reversal: after a receipt is posted, its article and warehouse are
  deactivated; reverse -> `201`.
- AC-66 Net zero: after a sequence of at least four postings (receipt, transfer, issue) and the reversal of
  each in an order stock allows, `GET /stock-on-hand` is empty, and for every reversed document the entries of
  the pair (original, reversing document) sum to zero per (article, warehouse).
- AC-67 Ledger equals stock (005/AC-60) still holds after a mix of receipts, issues, transfers and reversals.
- AC-68 Race: the same posted receipt reversed five times in parallel -> exactly one `201` and four `409
  INVALID_STATE`; exactly one reversing document exists (`GET /stock-documents?type=receipt` shows two
  documents); stock is 0, not negative.
- AC-69 Race: with exactly 10 of A in W1 from one receipt, reversing that receipt and posting a draft issue of
  10 in parallel -> exactly one of the two succeeds; the other gets `409 INSUFFICIENT_STOCK`; Stock(A, W1) is
  0 either way.
- AC-70 Lists: `status=reversed` returns the reversed originals only; `status=posted` includes reversing
  documents; an article on a reversed document still cannot be deleted (`409 IN_USE`).
- AC-71 `POST /stock-documents/{random UUID}/reverse` (valid body) -> `404` `NOT_FOUND`.

MCP
- AC-80 `tools/list` returns exactly 33 names (literal list). `stock_document_reverse` has a description, a
  closed `inputSchema` with `required == ["id", "documentDate"]`, an `outputSchema`, `destructiveHint == true`,
  `readOnlyHint == false`, `idempotentHint == false`. `stock_document_create` and `stock_document_update` have
  a described `toWarehouseId` property.
- AC-81 Through tools only: `stock_document_create` (transfer W1 -> W2, with stock) and `stock_document_post`
  -> tool success, `number == "ST-000001"`; `stock_document_reverse` -> tool success, `reversalOf.number ==
  "ST-000001"`; `stock_document_get` of the original -> `status == "reversed"`. Each result equals HTTP for the
  corresponding request; stock is back to the start.
- AC-82 Tool errors with the same `code` and `errors` keys as HTTP: `stock_document_create` transfer with
  `toWarehouseId` equal to `warehouseId` -> `VALIDATION_FAILED` (`toWarehouseId`); `stock_document_reverse` of
  a draft -> `INVALID_STATE`; of a receipt whose goods were issued -> `INSUFFICIENT_STOCK`
  (`lines[0].quantity`); with a date before the original's -> `VALIDATION_FAILED` (`documentDate`); with a
  random UUID -> `NOT_FOUND`.

Tenant isolation (tenants X and Y, each with the standard setup)
- AC-90 Y: a transfer with `toWarehouseId` = X's warehouse -> `409` `REFERENCE_NOT_FOUND` with key
  `toWarehouseId`.
- AC-91 Y: `POST /reverse` and `stock_document_reverse` on X's posted document -> `404` / tool error
  `NOT_FOUND`; X's document is still `posted` and X's stock unchanged.
- AC-92 Y's first transfer is `ST-000001` although X has one; X's reversal does not advance Y's numbers.

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on conservation (AC-32), net zero (AC-66), no negative stock by transfer and by reversal
  (AC-33, AC-60, AC-61) and the races (AC-38, AC-39, AC-68, AC-69).
- Approved changes to spec 005 tests are listed in AC-01; change nothing else there.

Builder
- A transfer is the posting of spec 005 with two pairs per line: lock the (article, warehouse) pairs of
  **both** warehouses in the same fixed order every posting uses, or AC-39 deadlocks.
- Reversal is a posting too: same transaction shape — lock the original, lock pairs, lock the counter, write
  the reversing document and entries, flip the original's status. The number is taken only when everything
  else has passed (AC-60).
- The per-tenant lock accepted for the MVP (ADR-0012, amendment of 2026-10-09) replaces the individual locks
  of the two notes above, provided reversal runs under it too.
- Derive the reversing entries from the original's ledger entries, not from its lines, so that R15 holds by
  construction.
- Anything unclear or contradictory: `docs/questions/006-q.md`, then continue with the rest.
