# Spec 008 — Stock count (adjustment)

Status: ready for the tester and the builder once spec 007 is merged to `main`.
Branches: `tests/008-stock-count` (tester), `feat/008-stock-count` (builder).
Read first: specs **005**, **006** and **007** (this spec extends them and repeats none of them), ADR-0012,
ADR-0013, ADR-0014, **ADR-0015**. Why this is the eighth spec: `docs/roadmap.md` section 3 — opening balances
and corrections make inventory usable on its own.

**Inherited, not re-specified** (roadmap §4): everything specs 001–007 established — transport, errors,
references, MCP mapping, and all rules for stock documents, lines and units, posting, reversal, numbering,
stock on hand and the ledger. This spec states only what is new or changed. Numbers are local; "005/R6" means
rule R6 of spec 005.

## 1. Goal

Let a tenant make stock in the system equal to stock on the shelf, without anyone editing a quantity.

- A **count** is a stock document of type `count`: for one warehouse, it lists articles and the quantity that
  was counted of each.
- The draft shows, per line, the **book quantity** it was prepared against and the **difference**.
- Posting writes the difference to the ledger; afterwards stock on hand of every counted article equals what
  was counted. If stock moved since the draft was saved, posting is refused instead of silently erasing the
  movement.
- A count on empty stock is how opening balances are entered.

## 2. Scope

In scope
1. Document type `count`: its lines, the book quantity, its posting and its reversal.
2. Two line properties on every stock document line; one error code.

Out of scope
- Freezing a warehouse during a count; a "full" count that zeroes unlisted articles; count sheets generated
  from stock on hand; several lines per article; reason codes; recounts and approval steps (ADR-0015).
- Adjustment by a known amount: that is a receipt or an issue (spec 005).
- Value of differences (011) and their accounting (012).

No new operation, route or tool: a count is created, replaced, deleted, posted, reversed, listed and read with
the operations of specs 005 and 006.

## 3. Data

`StockDocument.Type` gains `count`. `StockDocumentLine` gains `BookQuantity` numeric(18,6), null except on
count lines. `DocumentCounter` gets a fourth type, `count`. One migration.

## 4. Operations — HTTP

| Operation | Change |
|---|---|
| Create | `type` may be `"count"`. `toWarehouseId` must be omitted or `null`. A line's `quantity` is the counted quantity and may be `0`. |
| Replace | The same for a draft count. Saving records the book quantity of every line anew (R6). |
| List | `type=count` is a valid filter value. |
| Post | Additional error: `409 COUNT_OUTDATED`. A count never returns `INSUFFICIENT_STOCK`. |
| Reverse | Works for a posted count (R17–R19). |

A line in a representation gains two properties, always present, on every stock document:
```json
{ "…": "as spec 007 §4.3",
  "unit": { "code": "box", "…": "…" }, "quantity": 8, "factor": 12, "baseQuantity": 96,
  "bookQuantity": 100, "differenceQuantity": -4 }
```
Both are in the article's base unit; both are `null` unless the document's `type` is `"count"`.
A ledger entry's `document.type` may now be `"count"`.

New error code (registry: architecture §6):

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 409 | `COUNT_OUTDATED` | Posting a count: stock on hand of a counted article no longer equals the line's book quantity. | `lines[i].quantity` for every such line |

## 5. Operations — MCP

- `stock_document_create`, `stock_document_list`: `type` allows `"count"`. `stock_document_post` can return
  `COUNT_OUTDATED`. No new tool; `tools/list` still returns exactly the 37 tools of spec 007.
- The description of the `quantity` property of a `lines` item no longer says only "greater than 0": it says
  that on a count `0` is allowed. The schema itself is unchanged (a JSON number).
- The description of `stock_document_create` explains a count: `quantity` is what was counted, in the line's
  unit; `0` means none found; each article once; articles not listed are not changed; use it for opening
  balances; for a known difference use a receipt or an issue.
- The description of `stock_document_post` says for `COUNT_OUTDATED`: stock changed since the count was saved;
  read the document, check the count, save it again with `stock_document_update` (this takes the current book
  quantity) and post again.

## 6. Business rules

The document
- R1. `type` `"count"` records the counted quantities of articles in one warehouse, `warehouseId`.
  `toWarehouseId` must be omitted or `null` (006/R3). Header fields, references, the draft lifecycle and the
  order of checks are those of specs 005–007.
- R2. Lines: 1 to 200; each has `articleId`, `quantity` and optionally `unitId` (007/R12). Only `stock`
  articles (`ARTICLE_NOT_STOCKED`).
- R3. **Each article at most once.** Two or more lines with the same `articleId` -> `400` with key
  `lines[i].articleId` for every line of the repeated article. (On the other types repeats stay allowed.)
- R4. `quantity` on a count line is the counted quantity in the line's unit: a JSON number, **0 or greater**,
  otherwise as 005/R6. Negative -> `400` with key `lines[i].quantity`. On the other types `0` stays invalid.
- R5. `baseQuantity` is the counted quantity in base units by 007/R14. A counted quantity of `0` converts to
  `0`. A counted quantity greater than 0 whose `baseQuantity` would be 0 or above the maximum ->
  `QUANTITY_NOT_CONVERTIBLE` (007/R15).

Book quantity and difference
- R6. **The book quantity of a line is the stock on hand of its article in the document's warehouse at the
  moment the draft was last saved** (create or replace), in base units; 0 when there is none. Every save
  records it anew for all lines. It does not follow later movements: reading a draft never changes it.
- R7. `differenceQuantity` = `baseQuantity` − `bookQuantity`: positive when more was found than the books
  say, negative when less, 0 when they agree.
- R8. A draft count has no effect on stock or the ledger, reserves nothing and blocks no other posting.

Posting
- R9. Order of checks (extends 007/R18): document exists -> is a draft -> warehouse and articles active ->
  conversion -> **the count is current** (R10). There is no sufficiency check.
- R10. **A count is posted against the book quantity it shows.** If, at the moment of posting, stock on hand
  of any line's article in the warehouse differs from that line's `bookQuantity` -> `409 COUNT_OUTDATED` with
  key `lines[i].quantity` for every such line; nothing changes, no number is consumed. Only the quantity is
  compared: stock that moved and came back to the same value does not make a count outdated.
- R11. Posting writes, for every line whose `differenceQuantity` is not 0, one ledger entry with the line's
  `lineNo`, the document's warehouse and date, and `quantity` = `differenceQuantity`. A line without a
  difference writes no entry.
- R12. **After posting, stock on hand of every counted article in the warehouse equals the line's
  `baseQuantity`.** Stock of articles not on the count, and stock in other warehouses, is unchanged.
- R13. A count is never refused for stock reasons and never makes stock negative.
- R14. A posted count keeps `bookQuantity`, `differenceQuantity`, `factor` and `baseQuantity` of every line
  for good (007/R17). A count in which no line has a difference still posts: it gets a number and writes no
  entries.
- R15. Counts are numbered `SC-000001`, … from their own counter per tenant, by the rules of 005/R17.
- R16. Concurrency: R10 and R12 hold when a count is posted in parallel with other postings on the same
  pairs — the result is always that of some order of the postings, each judged against the stock the earlier
  ones left (005/R16).

Reversal
- R17. A posted count is reversed by the rules of spec 006. The reversing document is a `count` whose lines
  are copies of the original's posted lines, including `bookQuantity` and `differenceQuantity`; its entries
  are the original's with the opposite sign (006/R14); its number is the next `SC-` number.
- R18. Reversing a count is refused with `409 INSUFFICIENT_STOCK` if it would take a pair below zero
  (006/R16): a line that added stock needs that quantity still on hand. Keys `lines[i].quantity`.
- R19. A reversal undoes the count's entries; it does not restore the counted or the book quantity. After it,
  stock is what it would be had the count never been posted (006/R15).

Masters and reading
- R20. A count uses its warehouse and articles like any stock document (005/R24–R27), draft or posted.
- R21. Stock on hand is still exactly the sum of the ledger (005/R19).

## 7. Edge cases

- E1. A count line with `quantity` `0` -> accepted; `-1`, `"5"`, `null`, `1.0000001` -> `400`.
- E2. A count with `toWarehouseId` set -> `400` with key `toWarehouseId`.
- E3. A count of an article never received in that warehouse: book 0, difference = counted.
- E4. Counted 0 of an article with stock: difference = −stock; after posting the pair is no longer listed in
  stock on hand.
- E5. Counted 0 of an article with no stock: difference 0; posts; no entry.
- E6. A draft count read after stock moved still shows the old `bookQuantity`; `PUT` with the same body
  refreshes it and the difference.
- E7. A count with two lines of which one is outdated -> `COUNT_OUTDATED` with only that line's key; nothing
  is posted, not even the current line.
- E8. Two draft counts of the same pair: the first posts; the second is outdated if the first changed stock.
- E9. A factor changed between saving and posting changes `baseQuantity` and `differenceQuantity` of the
  draft (007/R17) but not `bookQuantity`; posting writes the difference as it is then.
- E10. `PUT` on a draft cannot change `type`: a receipt cannot become a count.
- E11. A posted count: `PUT`, `DELETE`, second post -> `INVALID_STATE`; later movements do not change its
  lines.

## 8. Tenant isolation

- T1. The book quantity is the caller's tenant's stock only; another tenant's postings never make a count
  outdated and never change its book quantity.
- T2. Another tenant's count is non-existent (`404`) for get, replace, delete, post and reverse.
- T3. The count counter is per tenant. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Any tenant key may post a count (as 005/S2; restrictable from spec 017). A count can write stock off or
  on without a counterpart; `postedBy` attributes it, and `note` should say why.
- S2. A count changes stock only through ledger entries; no operation sets a quantity (005/S3 unchanged).

## 10. Acceptance criteria

Conventions, "the standard setup", "receive n of A into W1" and "Stock(A, W1)" as in spec 005 §10; unit `box`
with A / box = 12 as in spec 007 §10. "Count n of A in W1" means: create a count for W1 with one line (A, n).
Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The only earlier tests changed are:
  tests asserting the exact property set of a stock document **line** (lines gain `bookQuantity`,
  `differenceQuantity`); tests pinning the exact enum values of `type` in a tool schema or expecting `type`
  `"count"` to be invalid. (No table is added, so the table test does not change.) The literal tool list is **not** changed.
  One migration added.
- AC-02 *(builder, unit)* The difference rule and the "current" rule (R7, R10, R11) are unit-tested without
  HTTP.

Inherited behaviour — smoke
- AC-10 A count with an unknown line property (`"bookQuantity": 5`) -> `400`; `GET /stock-documents?type=count`
  -> `200`.

Drafts
- AC-20 With 100 of A in W1: `POST /stock-documents` `{ "type": "count", "documentDate": "2026-10-09",
  "warehouseId": W1, "lines": [ { "articleId": A, "quantity": 97 } ] }` -> `201`; `type == "count"`,
  `status == "draft"`, `toWarehouse == null`; the line has `quantity == 97`, `baseQuantity == 97`,
  `bookQuantity == 100`, `differenceQuantity == -3`. Stock(A, W1) is still 100 and the ledger has no new entry.
- AC-21 Lines of a receipt, an issue and a transfer have `bookQuantity == null` and
  `differenceQuantity == null` (properties present).
- AC-22 Validation, nothing created: a count with lines (A, 5), (B, 1), (A, 2) -> `400` with keys
  `lines[0].articleId` and `lines[2].articleId` and without `lines[1].articleId`; quantity `-1` -> `400` with
  key `lines[0].quantity`; `"toWarehouseId": W2` -> `400` with key `toWarehouseId`; service article S ->
  `409` `ARTICLE_NOT_STOCKED`. A receipt with quantity `0` is still `400`; a receipt with the same article on
  two lines is still `201`.
- AC-23 Count 0 of A in W1 (stock 100) -> `201`, `baseQuantity == 0`, `differenceQuantity == -100`.
  Count 5 of B in W1 where B was never received -> `bookQuantity == 0`, `differenceQuantity == 5`.
- AC-24 Units: with 100 of A in W1, a count line (A, 8, box) -> `quantity == 8`, `factor == 12`,
  `baseQuantity == 96`, `bookQuantity == 100`, `differenceQuantity == -4`.
- AC-25 The book quantity is taken at save: after AC-20, receive 10 of A into W1. `GET` of the draft ->
  `bookQuantity == 100`, `differenceQuantity == -3`. `PUT` with the same values -> `200`,
  `bookQuantity == 110`, `differenceQuantity == -13`.
- AC-26 The book quantity is per warehouse: with 100 of A in W1 and 40 in W2, a count of A in W2 has
  `bookQuantity == 40`.

Posting
- AC-30 Post the draft of AC-20 -> `200`; `status == "posted"`, `number == "SC-000001"`; the line still has
  `bookQuantity == 100`, `differenceQuantity == -3`. Stock(A, W1) is 97. The ledger for the document has
  exactly one entry: `quantity == -3`, `lineNo == 1`, `document.type == "count"`,
  `document.number == "SC-000001"`.
- AC-31 Surplus: with 100 of A in W1, count 104 and post -> one entry `+4`; Stock(A, W1) is 104.
- AC-32 Opening balance: in a tenant with no postings, a count for W1 with lines (A, 50), (B, 20.5) posts ->
  entries `+50` and `+20.5`; Stock(A, W1) is 50, Stock(B, W1) is 20.5.
- AC-33 No difference: with 100 of A and 7 of B in W1, a count with lines (A, 100), (B, 5) posts -> the
  document has a number; its ledger has exactly one entry, `lineNo == 2`, `quantity == -2`. A count with the
  single line (A, 100) posts, gets the next `SC-` number and has no ledger entries; stock is unchanged.
- AC-34 Count to zero: with 100 of A in W1, count 0 and post -> entry `-100`; the pair is no longer in
  `GET /stock-on-hand`.
- AC-35 A count is partial: with stock of A and B in W1 and of A in W2, posting a count of A in W1 leaves
  Stock(B, W1) and Stock(A, W2) unchanged.
- AC-36 In units: post the count of AC-24 -> entry `-4`; Stock(A, W1) is 96.
- AC-37 **Stock equals the count.** After posting a count with several lines (surplus, shortage, no
  difference, zero, a line in boxes), for every line Stock(article, W1) equals the line's `baseQuantity`, and
  for every pair the ledger sum equals Stock(pair) (005/AC-60).
- AC-38 Posted is immutable: `PUT` (valid body), `DELETE` and a second post on a posted count -> `409`
  `INVALID_STATE`. After a later receipt of A, the posted count's line still shows the same `bookQuantity`
  and `differenceQuantity`.
- AC-39 Posting needs active masters: a draft count whose article was deactivated -> `409`
  `REFERENCE_INACTIVE` with key `lines[0].articleId`; after reactivation it posts.

Outdated counts
- AC-40 With 100 of A in W1, a draft count of 97. An issue of 10 is posted. Posting the count -> `409`
  `COUNT_OUTDATED` with key `lines[0].quantity`; the count is still a draft with `number == null` and
  `bookQuantity == 100`; Stock(A, W1) is 90; no entry was written.
- AC-41 After AC-40, `PUT` the count with the same values -> `bookQuantity == 90`, `differenceQuantity == 7`;
  posting -> `200`, `number == "SC-000001"` (the refused posting consumed no number), entry `+7`,
  Stock(A, W1) is 97.
- AC-42 Only the changed lines: a draft count with lines (A, 97), (B, 5); then only B's stock in W1 changes.
  Posting -> `COUNT_OUTDATED` with key `lines[1].quantity` and without `lines[0].quantity`; Stock(A, W1) is
  unchanged (nothing was posted).
- AC-43 Only the quantity matters: a draft count of A at book 100; an issue of 10 and a receipt of 10 are
  posted; posting the count -> `200`. Movements of A in W2 do not make a count in W1 outdated.
- AC-44 Two counts: with 100 of A in W1, draft counts of 97 and of 95. Post the first -> Stock is 97. Post the
  second -> `COUNT_OUTDATED`. After `PUT` (same values) it posts with entry `-2`; Stock is 95.
- AC-45 Race, count against issue: with exactly 10 of A in W1, a draft count of 4 and a draft issue of 8,
  posted in parallel -> exactly one `200`. Either the count succeeded, the issue got `409
  INSUFFICIENT_STOCK` and Stock(A, W1) is 4; or the issue succeeded, the count got `409 COUNT_OUTDATED` and
  Stock(A, W1) is 2. No other outcome; repeated at least ten times.
- AC-46 Race, same count: one draft count posted five times in parallel -> exactly one `200` and four `409
  INVALID_STATE`; one entry; one number.

Numbering and lists
- AC-50 Counts get `SC-000001`, `SC-000002` independently of `SR-`, `SI-` and `ST-` numbers.
  `GET /stock-documents/by-number/SC-000001` returns the count.
- AC-51 `GET /stock-documents?type=count` returns only counts. `GET /stock-ledger-entries?documentId=` of a
  posted count returns its entries in `lineNo` order, lines without a difference absent.

Reversal
- AC-60 With 100 of A in W1, count 97, post, then reverse (`documentDate` not earlier) -> `201`;
  `type == "count"`, `number == "SC-000002"`, `reversalOf.number == "SC-000001"`; its line has
  `quantity == 97`, `bookQuantity == 100`, `differenceQuantity == -3`; its ledger entry is `+3` with
  `document.isReversal == true`; Stock(A, W1) is 100; the original is `reversed`.
- AC-61 No negative stock by reversal: opening count of 50 of A in W1 (entry `+50`), then an issue of 30.
  Reversing the count -> `409` `INSUFFICIENT_STOCK` with key `lines[0].quantity`; the count is still
  `posted`; Stock(A, W1) is 20.
- AC-62 Reversing a count that wrote no entries -> `201`; the reversing document has no entries; stock is
  unchanged.
- AC-63 Net zero: for a reversed count with several lines, the entries of the pair (original, reversing
  document) sum to zero per article.

Effects on masters
- AC-70 An article and a warehouse on a draft count: `DELETE` of each -> `409` `IN_USE`; after the draft is
  deleted -> `204`. On a posted count they stay `IN_USE`.

MCP
- AC-80 `tools/list` returns exactly the 37 names of spec 007. `type` of `stock_document_create` and of
  `stock_document_list` allows `"count"`; the descriptions of `stock_document_create` and
  `stock_document_post` mention counts and `COUNT_OUTDATED`.
- AC-81 Through tools only: `stock_document_create` (count of A, 97, with stock 100) -> tool success,
  `differenceQuantity == -3`; `stock_document_post` -> tool success, `number == "SC-000001"`;
  `stock_on_hand_list` -> 97; `stock_document_reverse` -> tool success; stock is 100. Each result equals HTTP
  for the corresponding request.
- AC-82 Tool errors with the same `code` and `errors` keys as HTTP: `stock_document_post` of an outdated
  count -> `COUNT_OUTDATED` (`lines[0].quantity`); `stock_document_create` of a count with a repeated article
  -> `VALIDATION_FAILED` (`lines[0].articleId`, `lines[1].articleId`); with quantity `-1` ->
  `VALIDATION_FAILED` (`lines[0].quantity`); `stock_document_reverse` of a count whose surplus was issued ->
  `INSUFFICIENT_STOCK`.

Tenant isolation (tenants X and Y, each with the standard setup)
- AC-90 X has 100 of its A in its W1. Y's count of its own A in its own W1 has `bookQuantity == 0`; posting it
  with 5 gives Stock 5 in Y and leaves X at 100.
- AC-91 Y has a draft count; X then posts receipts and issues of articles with the same codes. Y's count
  still posts (`200`): another tenant's movements never make it outdated.
- AC-92 Y: `GET`, `PUT`, `DELETE`, `POST /post` and `POST /reverse` on X's count -> `404` `NOT_FOUND`; through
  tools -> `NOT_FOUND`. Y's first count is `SC-000001` although X has one.

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on "Posting" and "Outdated counts": stock equals the count (AC-37), the difference shown is
  the difference posted (AC-30, AC-41), a stale count is refused and changes nothing (AC-40, AC-42), and the
  race has only two outcomes (AC-45).
- Approved changes to earlier tests are listed in AC-01; the tool list stays as spec 007 left it.

Builder
- Posting a count is the posting of spec 005 with one more check: inside the transaction, after locking the
  pairs in the fixed order, compare each pair's stock with the line's `BookQuantity`, then write the
  differences. The comparison without the locks fails AC-45. (Or under the per-tenant lock: ADR-0012,
  amendment of 2026-10-09.)
- `BookQuantity` is written by create and replace only; reading a draft computes `differenceQuantity` from
  the stored book quantity and the current `baseQuantity`.
- Reversal needs nothing new: it negates the original's ledger entries (006). Copy `BookQuantity` to the
  reversing lines.
- The validation that differs by type (R3, R4) is Domain/Application code, decided from the request alone.
- Anything unclear or contradictory: `docs/questions/008-q.md`, then continue with the rest.
