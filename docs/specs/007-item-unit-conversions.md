# Spec 007 — Item unit conversions

Status: ready for the tester and the builder once spec 006 is merged to `main`.
Branches: `tests/007-item-unit-conversions` (tester), `feat/007-item-unit-conversions` (builder).
Read first: specs 002, **005** and **006** (this spec extends them and repeats none of them), ADR-0008,
ADR-0012, **ADR-0014**. Why this is the seventh spec: `docs/roadmap.md` section 3 — purchase and sales lines
(009, 010) need "bought by the box, stocked by the piece".

**Inherited, not re-specified** (roadmap §4): everything specs 001–006 established — transport, errors,
references, MCP mapping, and all rules for stock documents, posting, reversal, numbering, stock on hand and
the ledger. This spec states only what is new or changed. Numbers are local; "005/R6" means rule R6 of spec 005.

## 1. Goal

Let a tenant say, per article, that it is also handled in other units — *1 box = 12 pcs* — and enter stock
document lines in any of those units, while the ledger and stock on hand stay in the article's base unit.

- An **article unit** (conversion) gives one alternative unit of one article and its `factor` to the base unit.
- A **stock document line** may name a unit of its article; the document shows what was entered and what it
  is in base units; posting writes base quantities.
- Every stock invariant of specs 005 and 006 — ledger equals stock, no negative stock, conservation, net zero —
  holds unchanged, on base quantities.

## 2. Scope

In scope
1. `ArticleUnit`: list, get, set (create or replace), delete — each as an HTTP endpoint and an MCP tool.
2. Stock document lines: optional `unitId`; new line properties; conversion at posting.
3. Changes to existing operations: `DELETE /units-of-measure/{id}` and `PUT /articles/{id}` get further
   `IN_USE` cases; `GET /articles` gets the filter `alternativeUnitId`.

Out of scope
- Global conversions between units and unit categories; rounding precision per unit ("whole pieces only");
  default purchase and sales units; barcodes per unit (ADR-0014, alternatives).
- Units on anything but stock document lines (purchase 009, sales 010 reuse this spec's line fields).
- Stock on hand or ledger queries expressed in another unit: both stay in base units.

## 3. Data

`ArticleUnit` (tenant-owned): `TenantId`, `ArticleId`, `UnitId`, `Factor` numeric(12,6), audit columns.
Unique `(TenantId, ArticleId, UnitId)`; foreign keys to `Article` and `UnitOfMeasure` include `TenantId` and
restrict deletes; an article's conversions are deleted with the article by the application, not by cascade.

`StockDocumentLine` gains `UnitId` (required; foreign key to `UnitOfMeasure` with `TenantId`, restrict), and
`Factor` and `BaseQuantity`, set at posting. One migration; it gives every existing line its article's base
unit, and every existing posted line factor 1 and a base quantity equal to its quantity.

`StockLedgerEntry` is unchanged: its `Quantity` is, as before, in the article's base unit.

## 4. Operations — HTTP

### 4.1 Article units

Representation (`ArticleUnit`):
```json
{ "article": { "id": "uuid", "code": "ART-001", "name": "Steel bolt M8" },
  "unit": { "id": "uuid", "code": "box", "name": "Box" },
  "factor": 12,
  "baseUnit": { "id": "uuid", "code": "pcs", "name": "Piece" },
  "createdAt": "…Z", "updatedAt": "…Z", "createdBy": "uuid", "updatedBy": "uuid" }
```
Read as: *1 `unit` = `factor` × `baseUnit`*.

| Operation | Request | Success | Errors |
|---|---|---|---|
| List | `GET /articles/{articleId}/units?limit=&offset=` | `200` list envelope of ArticleUnit | 400, 404 |
| Get | `GET /articles/{articleId}/units/{unitId}` | `200` ArticleUnit | 404 |
| Set | `PUT /articles/{articleId}/units/{unitId}` body `{ "factor": number }` | `201` ArticleUnit and `Location` when the conversion was created; `200` ArticleUnit when it was replaced | 400, 404, 409 `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `UNIT_IS_BASE_UNIT` |
| Delete | `DELETE /articles/{articleId}/units/{unitId}` | `204` | 404, 409 `IN_USE` |

`404` on List and Set means the article does not exist; on Get and Delete, that the article or the conversion
does not exist. A path segment that is not a UUID -> `404` (001/E6). The list is ordered by unit code
(case-insensitive) and does not contain the base unit.

### 4.2 Changes to articles and units of measure

- `GET /articles?alternativeUnitId=` filters to articles that have a conversion for that unit (rules as
  `baseUnitId`, 002/R19). The Article representation is unchanged.
- `PUT /articles/{id}`: changing `baseUnitId` of an article that has conversions -> `409 IN_USE` (R9).
- `DELETE /articles/{id}`: an article that may be deleted (005/R25) is deleted together with its conversions.
- `DELETE /units-of-measure/{id}` -> `409 IN_USE` also while a conversion or a stock document line names the
  unit (R10).

### 4.3 Changes to the stock document

A line in a create or replace body is `{ "articleId", "quantity", "unitId"? }`. A line in a representation:
```json
{ "lineNo": 1, "article": { … },
  "unit": { "id": "uuid", "code": "box", "name": "Box" }, "quantity": 5,
  "factor": 12,
  "baseUnit": { "id": "uuid", "code": "pcs", "name": "Piece" }, "baseQuantity": 60 }
```
`unit` and `quantity` are what was entered; `factor`, `baseUnit` and `baseQuantity` are always present.
Additional errors on create, replace and post: `409 UNIT_NOT_ON_ARTICLE` (not on post),
`409 QUANTITY_NOT_CONVERTIBLE`; `REFERENCE_NOT_FOUND` / `REFERENCE_INACTIVE` also with key `lines[i].unitId`.

Stock on hand and ledger entries are unchanged: `unit` there is the base unit, `quantity` a base quantity.

### 4.4 New error codes (registry: architecture §6)

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 409 | `UNIT_IS_BASE_UNIT` | A conversion is set for the article's own base unit. | `unitId` |
| 409 | `UNIT_NOT_ON_ARTICLE` | A line's unit exists but is neither the base unit nor an alternative unit of the line's article. | `lines[i].unitId` |
| 409 | `QUANTITY_NOT_CONVERTIBLE` | A line's quantity converts to a base quantity of zero or above the maximum. | `lines[i].quantity` |
| 409 | `IN_USE` *(extended)* | Also: delete of a conversion used by a draft line; change of `baseUnitId` of an article with conversions. | — / `baseUnitId` |

## 5. Operations — MCP

| Tool | Arguments | Success | HTTP operation | Error codes |
|---|---|---|---|---|
| `article_unit_list` | `{ articleId, limit?, offset? }` | list envelope | List | `VALIDATION_FAILED`, `NOT_FOUND` |
| `article_unit_get` | `{ articleId, unitId }` | ArticleUnit | Get | `NOT_FOUND` |
| `article_unit_set` | `{ articleId, unitId, factor }` | ArticleUnit | Set | as HTTP |
| `article_unit_delete` | `{ articleId, unitId }` | `{ "deleted": true }` | Delete | `NOT_FOUND`, `IN_USE` |

- `articleId` and `unitId` of these four tools are addressing arguments: 003/R15 applies to each under its own
  name (`errors.articleId`, `errors.unitId`).
- Annotations: `_list`, `_get` read-only; `article_unit_set` as `*_update` (`destructiveHint: true`,
  `idempotentHint: true`); `article_unit_delete` as `*_delete` (003 §5.3).
- The description of `article_unit_set` states the direction of the factor with an example (*1 box = 12 pcs ->
  unitId = box, factor = 12*), that the call creates or replaces, that a changed factor affects drafts and
  future documents only, and that a ratio which is not an exact decimal is better avoided by making the
  smaller unit the base unit.
- `stock_document_create` / `_update`: a `lines` item is `{ articleId, quantity, unitId? }` (`unitId`: uuid or
  null; closed schema); the descriptions say that `unitId` defaults to the base unit and that stock is always
  kept in base units. `article_list` gains `alternativeUnitId?`.
- `uom_delete`, `article_update`: descriptions mention the new `IN_USE` cases.
- `tools/list` returns exactly 37 tools: the 33 of spec 006 and these 4.

## 6. Business rules

Conversions
- R1. A conversion says: one `unit` of this article equals `factor` of the article's base unit. It belongs to
  one article; the same unit may have different factors on different articles. Both article types may have
  conversions.
- R2. `factor` is required: a JSON number greater than 0, with at most 6 decimal places, at most
  999999.999999. Violations -> `400` with key `factor`. A factor of exactly 1 is allowed.
- R3. Set creates the conversion if the article has none for that unit and replaces its `factor` otherwise;
  repeating the same request is harmless. Audit fields as on any record (001/R7).
- R4. The unit follows the reference rules with key `unitId`: no such unit in the tenant ->
  `409 REFERENCE_NOT_FOUND`; an inactive unit when the conversion is being created -> `409 REFERENCE_INACTIVE`
  (replacing the factor of an existing conversion whose unit was deactivated since succeeds).
- R5. The article's base unit cannot be given a conversion -> `409 UNIT_IS_BASE_UNIT`. The base unit is always
  a unit of the article, with factor 1.
- R6. Order of checks on Set: validation -> article exists (`404`) -> unit exists -> unit is not the base unit
  -> unit active (when creating).
- R7. A factor can be changed at any time, used or not (R17 says what the change affects).
- R8. Delete removes the conversion. A conversion that a line of a **draft** stock document uses ->
  `409 IN_USE`, unchanged. Lines of posted documents do not block it.

Effects on masters
- R9. While an article has at least one conversion its `baseUnitId` is frozen: a `PUT /articles/{id}` that
  changes it -> `409 IN_USE` with key `baseUnitId`, nothing changed (as 005/R26, same place in the order of
  checks). `type` is not frozen by conversions.
- R10. A unit of measure is used — `DELETE` -> `409 IN_USE` — while it is the base unit of an article (002),
  the unit of a conversion, or the unit of a stock document line, draft or posted. It can still be renamed,
  re-coded and deactivated.
- R11. Deleting an article deletes its conversions with it; the units become unused by them.

Lines
- R12. A line may carry `unitId`. Omitted or `null` means the article's base unit. A given `unitId` must be a
  UUID string (`400`, key `lines[i].unitId`), must exist in the tenant (`REFERENCE_NOT_FOUND`), must not be an
  inactive unit newly assigned (`REFERENCE_INACTIVE`; "newly" as 005/R5: the stored draft has no line with that
  unit), and must be the base unit or an alternative unit of the line's article (`UNIT_NOT_ON_ARTICLE`) — all
  with key `lines[i].unitId`. **The article's base unit is never checked for being active on a line**,
  whether it is given or omitted: it is the article's own unit (002/R9 lets an article keep a base unit
  deactivated since), and specs 005 and 006 as built do not check it. `REFERENCE_INACTIVE` on a line's unit
  therefore concerns every unit other than the article's base unit — whether or not it is an alternative
  unit of the article: a newly assigned inactive unit that is not a unit of the article at all is
  `REFERENCE_INACTIVE`, not `UNIT_NOT_ON_ARTICLE` (R16 orders the kinds; `007-q.md`, T-Q6).
- R13. `quantity` is in the line's unit and obeys 005/R6 unchanged.
- R14. **Conversion.** `baseQuantity` = `quantity` × `factor`, rounded to 6 decimal places, half away from
  zero. For the base unit, `factor` is 1 and `baseQuantity` equals `quantity`.
- R15. A line whose `baseQuantity` would be 0 or greater than 999999999.999999 ->
  `409 QUANTITY_NOT_CONVERTIBLE` with key `lines[i].quantity`.
- R16. Order of checks on create and replace (extends 005/R8): … line references in line order, the first
  failing kind reported for all lines that have it: `REFERENCE_NOT_FOUND` (article or unit), then
  `REFERENCE_INACTIVE`, then `ARTICLE_NOT_STOCKED`, then `UNIT_NOT_ON_ARTICLE`, then
  `QUANTITY_NOT_CONVERTIBLE`.
- R17. **A draft follows the article's current factor; a posted line keeps its own.** `factor` and
  `baseQuantity` of a draft line are computed with the conversion as it is when the document is read. Posting
  computes them once more, with the factor at that moment, and stores them on the line; from then on they
  never change, whatever happens to the conversion. A change of factor writes nothing to a draft: its
  `updatedAt` / `updatedBy` do not change. Reading never fails on R15: a draft line that no longer converts
  shows the current `factor` and the `baseQuantity` R14 gives (`0`, or a value above the maximum); only
  saving and posting refuse it (`007-q.md`, T-Q7).
- R18. Posting (extends 005/R13 and 006/R5): … masters active -> conversion (`QUANTITY_NOT_CONVERTIBLE`, R15,
  with the current factors) -> stock. Units are not re-checked for being active.
- R19. **The ledger is in base units.** Every ledger entry a posted line produces has `quantity` equal to
  plus or minus that line's `baseQuantity` (005/R14, 006/R6). Sufficiency (005/R15, 006/R7) compares sums of
  `baseQuantity` with stock on hand.
- R20. Reversal (006/R13, R14): the reversing document's lines are copies of the original's posted lines —
  unit, quantity, `factor` and `baseQuantity` as posted — whatever the conversion is now, and even if it no
  longer exists. Net zero (006/R15) therefore holds after any change of factor.
- R21. Stock on hand and the ledger show the base unit and base quantities only.

## 7. Edge cases

- E1. `factor` missing, `null`, `0`, `-1`, `"12"`, `1.0000001`, `1000000` -> `400` with key `factor`;
  `0.000001` and `999999.999999` -> accepted. Body with another property (`"unitId"`, `"isActive"`) -> `400`.
- E2. Set with the base unit's id -> `UNIT_IS_BASE_UNIT`; with a random unit UUID -> `REFERENCE_NOT_FOUND`;
  with a random article UUID -> `404`, also when the unit is unknown too.
- E3. Two parallel Set requests for the same new conversion: one `201`, the other `201` or `200`; exactly one
  conversion exists afterwards, with one of the two factors; never `500`.
- E4. A line with `unitId` of a unit that is an alternative unit of another article only ->
  `UNIT_NOT_ON_ARTICLE`.
- E5. A line with the base unit's id given explicitly behaves exactly as a line without `unitId`, also when
  the base unit has been deactivated (R12).
- E6. The same article on two lines in different units (2 box, 6 pcs) is allowed; each line converts on its own.
- E7. A draft line in boxes; the factor changes from 12 to 10: the draft now shows `factor` 10 and the new
  `baseQuantity`, and posts that.
- E8. A draft line whose quantity was convertible when saved and is not after the factor changed -> posting
  gives `QUANTITY_NOT_CONVERTIBLE`; the draft stays a draft.
- E9. A conversion deleted after its only document was posted: the posted document still shows unit, factor
  and base quantity; it can still be reversed; the unit of measure still cannot be deleted.
- E10. An article whose base unit must change: delete its conversions, change the base unit (possible only
  while no stock document uses the article, 005/R26), set the conversions again.
- E11. An article unused by documents but with conversions -> `DELETE /articles/{id}` succeeds.

## 8. Tenant isolation

- T1. `ArticleUnit` is tenant-owned; its foreign keys and the new one on lines include `TenantId`.
- T2. Another tenant's article in the path -> `404 NOT_FOUND` on all four operations; another tenant's unit ->
  `REFERENCE_NOT_FOUND` on Set and on a line (key `lines[i].unitId`), `404` on Get and Delete; as a filter ->
  empty result.
- T3. Conversions are per tenant: equal codes in two tenants are unrelated. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. Any tenant key may maintain conversions (restrictable from spec 017). `updatedBy` shows who last set a
  factor; the history of factor changes is the audit log's (016).
- S2. A change of factor never updates a ledger entry or a posted line (005/S3 unchanged).

## 10. Acceptance criteria

Conventions, "the standard setup", "receive n of A into W1" and "Stock(A, W1)" as in spec 005 §10. In addition
this spec's setup has units `box` and `pack` and the conversion **A / box = 12** (set through the API); B has
no conversions. Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The only earlier tests changed are:
  the literal tool list; the table test's list of named tables (it gains `ArticleUnits`; the test itself asserts
  the tenant invariant on every table, architecture §9); tests asserting the exact property set of a
  stock document **line** (specs 005, 006: lines gain `factor`, `baseUnit`, `baseQuantity`); tests that used
  `unitId` as the example of an unknown line property (005/E2 — another name is used instead); tests pinning
  the exact input-schema properties of `article_list` or of a `lines` item. One migration added.
- AC-02 *(builder, unit)* The conversion rule (R14, R15) is unit-tested without HTTP, including half-way
  values, zero results and the maximum.
- AC-03 *(builder)* After the migration, a line and a posted line that existed before have the article's base
  unit, and the posted one factor 1 and `baseQuantity == quantity`.

Inherited behaviour — smoke
- AC-10 `GET /articles/{A}/units` and `PUT /articles/{A}/units/{box}` without a credential -> `401`; with the
  admin key -> `403`; `GET /articles/{A}/units?x=1` -> `400` with key `x`; `PUT` with body
  `{ "factor": 12, "isActive": true }` -> `400`.

Conversions
- AC-20 `PUT /articles/{A}/units/{pack}` `{ "factor": 6 }` -> `201`; `article.id == A`, `unit.id == pack` with
  its code and name, `factor == 6`, `baseUnit.code == "pcs"`, `createdBy == updatedBy ==` the acting key;
  `Location` ends with `/api/v1/articles/{A}/units/{pack}`; `GET` of that path returns the same.
- AC-21 The same `PUT` with `{ "factor": 8 }` by another key K2 -> `200`, `factor == 8`, `updatedBy == K2`,
  `createdAt` and `createdBy` unchanged. Repeating it -> `200`, same `factor`.
- AC-22 `GET /articles/{A}/units` -> the conversions for `box` and `pack`, ordered by unit code, `total == 2`,
  none for `pcs`; `GET /articles/{B}/units` -> `200`, empty; `GET /articles/{random UUID}/units` -> `404`.
- AC-23 Factor validation, each `400` with key `factor`, nothing changed: missing, `null`, `0`, `-1`, `"12"`,
  `1.0000001`, `1000000`. `0.000001` and `999999.999999` -> accepted.
- AC-24 References on Set: random unit UUID -> `409` `REFERENCE_NOT_FOUND` with key `unitId`; an inactive
  unit -> `409` `REFERENCE_INACTIVE` with key `unitId`; A's base unit -> `409` `UNIT_IS_BASE_UNIT` with key
  `unitId`; random article UUID -> `404`; `PUT /articles/{A}/units/abc` -> `404`. No conversion was created.
- AC-25 After unit `box` is deactivated, `PUT /articles/{A}/units/{box}` `{ "factor": 10 }` -> `200`.
- AC-26 `DELETE /articles/{A}/units/{pack}` -> `204`; `GET` -> `404`; a second `DELETE` -> `404`.
- AC-27 A conversion on service article S -> `201`. A factor of exactly `1` -> `201`.
- AC-28 The same unit on two articles with different factors (A / box = 12, B / box = 50): each `GET` returns
  its own factor.
- AC-29 `GET /articles?alternativeUnitId={box}` returns exactly the articles that have a conversion for `box`
  (not articles whose base unit it is); a random UUID -> `200`, empty; `abc` -> `400` with key
  `alternativeUnitId`. `GET /articles/{A}` has the same property set as before this spec.

Effects on masters
- AC-30 `DELETE /units-of-measure/{box}` while A / box exists -> `409` `IN_USE`; after the conversion is
  deleted (and no line uses the unit) -> `204`.
- AC-31 An article with a conversion and no stock documents: `PUT /articles/{id}` changing `baseUnitId` ->
  `409` `IN_USE` with key `baseUnitId`, article unchanged; changing only `type` or `name` -> `200`. After its
  conversions are deleted, changing `baseUnitId` -> `200`.
- AC-32 An article with conversions and no stock documents: `DELETE /articles/{id}` -> `204`; afterwards a
  unit that only its conversions used (not a base unit, on no other conversion or line) can be deleted (`204`).
- AC-33 A draft with a line in boxes: `DELETE /articles/{A}/units/{box}` -> `409` `IN_USE`. After the draft is
  replaced with the line in the base unit (or deleted) -> `204`.
- AC-34 A posted document with a line in boxes: `DELETE /articles/{A}/units/{box}` -> `204`;
  `GET` of the document still shows `unit.code == "box"`, `factor == 12` and the same `baseQuantity`;
  `DELETE /units-of-measure/{box}` -> `409` `IN_USE`.

Lines — drafts
- AC-40 A receipt with line `{ "articleId": A, "quantity": 5, "unitId": box }` -> `201`; the line has
  `unit.code == "box"`, `quantity == 5`, `factor == 12`, `baseUnit.code == "pcs"`, `baseQuantity == 60`.
- AC-41 A line without `unitId`, with `"unitId": null`, and with `"unitId"` = the id of `pcs` -> each `201`
  with `unit.code == "pcs"`, `factor == 1`, `baseUnit.code == "pcs"`, `baseQuantity == quantity`.
- AC-42 Line unit errors, nothing created: `"unitId": "abc"` -> `400` with key `lines[0].unitId`; a random
  UUID -> `409` `REFERENCE_NOT_FOUND` with key `lines[0].unitId`; an inactive **alternative** unit of A (`box`,
  deactivated after its conversion was set) -> `409` `REFERENCE_INACTIVE` with key `lines[0].unitId`; B with `box` (a unit of A only) -> `409`
  `UNIT_NOT_ON_ARTICLE` with key `lines[0].unitId`; A with `pack` when A has no conversion for it -> the same.
  After `pcs` (A's base unit) is deactivated, a line of A without `unitId` and one with `"unitId"` = `pcs` ->
  each `201` (R12).
- AC-43 Order of kinds: lines [(B, 1, box), (random article UUID, 1)] -> `REFERENCE_NOT_FOUND` with key
  `lines[1].articleId` and without `lines[0].unitId`.
- AC-44 `PUT` on a draft changing a line from boxes to the base unit and back -> `200` each time with the
  matching `unit`, `factor` and `baseQuantity`.

Lines — posting and the ledger
- AC-50 Post the receipt of AC-40 -> `200`; the ledger entry of the document has `quantity == 60` and
  `unit.code == "pcs"`; Stock(A, W1) is 60 and its `unit.code == "pcs"`.
- AC-51 A receipt with lines (A, 2, box), (A, 6) posted -> entries `+24` and `+6`; Stock(A, W1) is 30.
- AC-52 No negative stock, in base units: with 30 of A in W1, an issue of 3 box -> `409`
  `INSUFFICIENT_STOCK` with key `lines[0].quantity`, stock still 30; an issue with lines (A, 1, box), (A, 20)
  -> `INSUFFICIENT_STOCK` with both line keys; an issue of 2 box posts, entry `-24`, Stock(A, W1) is 6.
- AC-53 With 30 of A in W1, a transfer of 1 box to W2 -> entries `-12` in W1 and `+12` in W2;
  Stock(A, W1) is 18, Stock(A, W2) is 12.
- AC-54 Rounding: with A / pack = 0.333333, a receipt with lines (A, 0.5, pack) and (A, 1.5, pack) ->
  `baseQuantity` `0.166667` and `0.5`; after posting the two entries are `+0.166667` and `+0.5` and
  Stock(A, W1) is `0.666667`.
- AC-55 Not convertible: with A / pack = 0.4, a line (A, 0.000001, pack) -> `409`
  `QUANTITY_NOT_CONVERTIBLE` with key `lines[0].quantity`, nothing created; with A / pack = 0.5 the same line
  -> `201`, `baseQuantity == 0.000001`. With A / pack = 999999.999999, a line (A, 999999999, pack) ->
  `QUANTITY_NOT_CONVERTIBLE`.

Changing a factor
- AC-60 A draft receipt of 5 box (factor 12). Set A / box to 10: `GET` of the draft -> `factor == 10`,
  `baseQuantity == 50`. Post -> entry `+50`, Stock(A, W1) is 50.
- AC-61 After AC-60 set A / box to 20: the posted document still shows `factor == 10`, `baseQuantity == 50`;
  the ledger entry is still `+50`; Stock(A, W1) is still 50. A new draft of 1 box shows `baseQuantity == 20`.
- AC-62 After AC-61, reverse the posted receipt -> `201`; the reversing document's line has
  `unit.code == "box"`, `quantity == 5`, `factor == 10`, `baseQuantity == 50`; its entry is `-50`;
  Stock(A, W1) is 0. The same holds when the conversion was deleted before the reversal.
- AC-63 A draft with line (A, 0.000002, pack) saved while A / pack = 0.5; the factor is set to 0.2; posting ->
  `409` `QUANTITY_NOT_CONVERTIBLE` with key `lines[0].quantity`; the document is still a draft with
  `number == null`; no entry was written. After the factor is set back, posting succeeds.
- AC-64 Ledger in base units: after at least five postings of receipts, issues and a transfer with lines in
  `pcs`, `box` and `pack`, with a factor changed in between and one reversal: every posted line's ledger
  entries have `quantity == ±baseQuantity` of that line; for every pair the ledger sum equals Stock(pair)
  (005/AC-60); for the reversed pair of documents the entries sum to zero per (article, warehouse).

MCP
- AC-80 `tools/list` returns exactly 37 names (literal list). Each of the four new tools has a description, a
  closed `inputSchema` with described properties and `required` as in section 5, an `outputSchema` and its
  annotations. The `lines` item schema of `stock_document_create` and `_update` has a described `unitId`
  that is not required; `article_list` has `alternativeUnitId`.
- AC-81 Through tools only: `article_unit_set` (A, pack, 6) -> tool success, `factor == 6`; again with 8 ->
  tool success, `factor == 8`; `article_unit_get`, `article_unit_list` -> tool success;
  `stock_document_create` with a line in `pack` and `stock_document_post` -> `baseQuantity == 8 × quantity`;
  `article_unit_delete` -> `{ "deleted": true }`. Each result equals HTTP for the corresponding request.
- AC-82 Tool errors with the same `code` and `errors` keys as HTTP: `article_unit_set` with factor `0` ->
  `VALIDATION_FAILED` (`factor`); with the base unit -> `UNIT_IS_BASE_UNIT` (`unitId`); with a random unit ->
  `REFERENCE_NOT_FOUND` (`unitId`); with a random article -> `NOT_FOUND`; without `unitId` ->
  `VALIDATION_FAILED` (`unitId`); `article_unit_delete` of a conversion used by a draft -> `IN_USE`;
  `stock_document_create` with a unit not on the article -> `UNIT_NOT_ON_ARTICLE` (`lines[0].unitId`);
  `uom_delete` of a unit with a conversion -> `IN_USE`.

Tenant isolation (tenants X and Y, each with this spec's setup)
- AC-90 Y: `GET /articles/{X's A}/units`, `GET`, `PUT` (valid body) and `DELETE` on
  `/articles/{X's A}/units/{X's box}` -> `404` `NOT_FOUND`; through the four tools -> tool error `NOT_FOUND`.
  X's conversion is unchanged.
- AC-91 Y: `PUT /articles/{Y's A}/units/{X's pack}` -> `409` `REFERENCE_NOT_FOUND` with key `unitId`; a line
  with `unitId` = X's box -> `REFERENCE_NOT_FOUND` with key `lines[0].unitId`;
  `GET /articles?alternativeUnitId={X's box}` -> empty.
- AC-92 Y sets its own A / box to 50 while X's is 12: a receipt of 1 box posts `+50` in Y and `+12` in X.

Errors
- AC-98 Every HTTP error asserted above is `application/problem+json` with `code`, `status`, `title`,
  `detail`; no asserted response is `500` and no tool result has code `INTERNAL_ERROR`.

## 11. Notes for the tester and the builder

Tester
- The weight is on "Lines — posting and the ledger" and "Changing a factor": the ledger never moves when a
  factor does (AC-61), a reversal cancels exactly (AC-62), and stock rules count base quantities (AC-52).
- Approved changes to earlier tests are listed in AC-01; change nothing else there. Requests of specs 005 and
  006 that send no `unitId` must keep producing the same quantities and stock as before.

Builder
- One conversion function in Domain (AC-02), used by reading a draft, by saving (R15) and by posting (R17).
- Posting reads the factors inside the posting transaction and writes `Factor` and `BaseQuantity` on the lines
  with the ledger entries; ledger quantities come from `BaseQuantity`, never from `quantity × factor` a second
  time.
- The reversing document copies the stored `Factor` / `BaseQuantity`; it does not look at `ArticleUnit`.
- "Used by a draft line" (R8) must hold under a race between saving a draft and deleting the conversion:
  either the draft is saved and the delete gets `IN_USE`, or the delete succeeds and the save gets
  `UNIT_NOT_ON_ARTICLE`; never a draft line whose unit is not a unit of its article.
- As built (spec 005), every stock document write and `PUT /articles/{id}` run under one lock per tenant
  (ADR-0012, amendment of 2026-10-09). Set and Delete of a conversion must run under the same lock
  (condition 1 of the amendment); the race above and E3 are then settled by construction.
- As built, the tool-side address check (`RecordAddress.Id`) reports a missing id under the fixed name `id`.
  The four new tools need it under `articleId` / `unitId` (section 5): give the helper the argument's name.
- Anything unclear or contradictory: `docs/questions/007-q.md`, then continue with the rest.
