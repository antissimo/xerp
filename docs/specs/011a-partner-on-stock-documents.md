# Spec 011a — Partner on stock documents

Status: ready for the tester and the builder once spec 011 is merged to `main`. Asked for by the owner on
2026-10-10: the partner is part of a document's header (architecture §5, "Document model: header and lines").
Branches: `tests/011a-partner-on-stock-documents` (tester), `feat/011a-partner-on-stock-documents` (builder).
Read first: specs **005** (stock documents), 006 (reversal), **009** and 010 (the order link, the partner
roles), **011** as written (this spec builds on it), 004 (partners).
Why this spec and why this number: `docs/roadmap.md` section 3, row 11a.

**Inherited, not re-specified** (roadmap §4): everything specs 001–011 established — transport, errors,
references, MCP mapping, stock documents, posting, reversal, the order link, the default warehouse. This spec
states only what is new or changed. Numbers are local; "009/R21" means rule R21 of spec 009.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

A stock document says, in its own header, whom the goods came from or went to.

- A **receipt** may name a supplier, an **issue** a customer: optional `partnerId`, returned as `partner`.
- A receipt or delivery **linked to an order** always has the order's partner — it is taken from the order
  and cannot be another.
- Stock documents can be listed by partner. Nothing about stock, the ledger or the balances changes.

## 2. Scope

In scope
1. `partnerId` on create and replace of a stock document of type `receipt` or `issue`; `partner` in every
   stock document representation and list summary; list filter `partnerId`.
2. The partner of a linked document; the partner of a reversing document.
3. A further `IN_USE` case for the partner.
4. The same in the three MCP tools `stock_document_create`, `stock_document_update`, `stock_document_list`.

Out of scope
- A partner on transfers and counts; a partner per line (architecture §5: never).
- A **mandatory** partner on receipts and issues; returns (an issue to a supplier, a receipt from a customer)
  — a later spec; a delivery address or contact person on the document.
- Prices and value on stock documents (012); invoices (015), which will read the partner from here.
- Any change to orders, to stock on hand, to the ledger or to `StockBalance`.

## 3. Data

`StockDocument` gains `PartnerId` (nullable). Foreign key `(TenantId, PartnerId)` -> `Partner (TenantId, Id)`,
`ON DELETE RESTRICT`; index `(TenantId, PartnerId)`. No other table changes.

One migration. It sets `PartnerId` of every existing **linked** stock document — reversing documents
included — to the supplier of its purchase order or the customer of its sales order; unlinked documents keep
`null` (R17).

## 4. Operations — HTTP

No new operation. Changes to the stock document (005 §4.1, as changed by 006, 008, 009, 010, 011):

| Operation | Change |
|---|---|
| Create | Body gains `"partnerId"?` (uuid or `null`). For a `transfer` or a `count` it must be omitted or `null`. |
| Replace | Body gains `"partnerId"`: for a `receipt` or an `issue` it **must be present** (uuid or `null`); for a `transfer` or a `count` it may be omitted or `null`. |
| List | New filter `partnerId=`. |
| Post | Additional errors for an unlinked document: `409 REFERENCE_INACTIVE` with key `partnerId`, `409 PARTNER_ROLE_MISSING` (R12). |
| Reverse | The reversing document carries the original's partner (R14). The reverse body is unchanged. |
| `DELETE /partners/{id}` | `409 IN_USE` also while a stock document names the partner (R15). |

Representation: every stock document and list summary gains `"partner"`, always present:
`{ "id", "code", "name" }` of the partner, otherwise `null`.

Error codes — none is new; two get a further key (registry: architecture §6):

| HTTP | `code` | When, in this spec | `errors` keys |
|---|---|---|---|
| 400 | `VALIDATION_FAILED` | `partnerId` malformed; non-null on a transfer or count; missing in the replace body of a receipt or issue | `partnerId` |
| 409 | `REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE` | The partner of an unlinked document does not exist in the tenant / is inactive and newly assigned, or inactive at posting | `partnerId` |
| 409 | `PARTNER_ROLE_MISSING` *(extended)* | The partner of an unlinked receipt is not a supplier; of an unlinked issue, not a customer | `partnerId` |
| 409 | `ORDER_MISMATCH` *(extended)* | A linked document names a partner other than its order's | `partnerId` |

## 5. Operations — MCP

No new tool; `tools/list` still returns exactly the 57 tools of spec 011.

- `stock_document_create` gains `partnerId?` (uuid or null). `stock_document_update` gains `partnerId?` (uuid
  or null) — not in `required`, because it does not apply to every type; the server requires it for a
  receipt and an issue (`VALIDATION_FAILED`, key `partnerId`), as it does `toWarehouseId` for a transfer.
  `stock_document_list` gains `partnerId?` (uuid).
- The output schema of every tool that returns a stock document or its summary gains `partner`.
- Error codes are those of the HTTP operations; `stock_document_create` and `stock_document_update` add
  `PARTNER_ROLE_MISSING`, `stock_document_post` adds `PARTNER_ROLE_MISSING`.
- Descriptions say:
  - `stock_document_create` — `partnerId` is optional: the supplier of a receipt (a partner with
    `isSupplier`) or the customer of an issue (`isCustomer`); not allowed on a transfer or a count; on a
    document linked to an order leave it out — the order's partner is taken, and another one is refused.
  - `stock_document_update` — a receipt or issue must send `partnerId`; `null` removes the partner (on a
    linked document it stays the order's).
  - `stock_document_list` — `partnerId` finds the receipts from a supplier and the issues to a customer,
    linked to an order or not.
  - `partner_delete` — mentions the further `IN_USE` case.

## 6. Business rules

Which documents have a partner
- R1. Only a `receipt` and an `issue` can have a partner. On a `transfer` or a `count` a non-null `partnerId`
  -> `400` with key `partnerId`, on create and on replace; their `partner` is always `null`.
- R2. The partner is optional *(default)*: a receipt or issue without one is as valid as before this spec and
  behaves exactly as before.
- R3. `partnerId` is a uuid or `null`; anything else -> `400` with key `partnerId`. On create, omitted and
  `null` are equal. On replace of a receipt or an issue the property must be present (architecture §5:
  replace carries all fields) *(default)*; missing -> `400` with key `partnerId`; `null` means "no partner"
  (R8 for a linked document).
- R4. The partner is a header field: one per document, none on a line. A line with `partnerId` is a line
  with an unknown property (`400`).

An unlinked document (no `purchaseOrderId`, no `salesOrderId`)
- R5. `partnerId` follows the reference rules (ADR-0008): not a partner of the tenant ->
  `409 REFERENCE_NOT_FOUND`; inactive and newly assigned -> `409 REFERENCE_INACTIVE`; both with key
  `partnerId`. A replace that keeps the partner the draft already has is not a new assignment (005/E10 — a
  draft keeps a master deactivated since).
- R6. **Role** *(default)*. The partner of a receipt must have `isSupplier == true`, the partner of an issue
  `isCustomer == true`; otherwise `409 PARTNER_ROLE_MISSING` with key `partnerId`. The role is checked on
  every create and replace, also when the partner is kept (009/R2). A partner with both roles fits both.
- R7. Order of checks on create and replace (005/R8, extended): validation -> document exists -> is a draft
  -> header references: `REFERENCE_NOT_FOUND`, then `REFERENCE_INACTIVE` (warehouse and partner are both
  header masters; the keys that apply are reported together, as for the two warehouses of a transfer) ->
  `PARTNER_ROLE_MISSING` -> lines. Each stage answers alone.

A linked document
- R8. **The partner of a linked document is the order's partner**: the supplier of the purchase order on a
  receipt, the customer of the sales order on a delivery. On create and on replace, `partnerId` omitted (on
  create), `null`, or equal to the order's partner all give that partner. It is stored on the document and
  returned as `partner`. A linked document never has `partner == null`.
- R9. **It cannot differ.** Any other non-null `partnerId` -> `409 ORDER_MISMATCH` with key `partnerId`,
  together with the other keys of that code that apply (`warehouseId`, `lines[i].articleId`), at the place
  009/R21 gives the agreement check (after the order exists and is `confirmed`, and after the line kinds).
  The value is only compared: an id that is unknown, of another tenant, inactive or without the role gives
  the same `ORDER_MISMATCH`. Nothing is silently replaced.
- R10. The order's partner is not checked again on a linked document — neither activity nor role, at saving
  or at posting (009/R24: goods on the way are received even from a partner deactivated since). It cannot
  change either: the link needs a confirmed order, and a confirmed order is immutable (009/R14).

Posting
- R11. Posting does not change the partner and writes nothing about it to the ledger: entries, balances,
  numbers, stock on hand and order progress are exactly those of specs 005–011.
- R12. Posting an **unlinked** receipt or issue that has a partner checks it *(default)*: the partner is
  active — else `409 REFERENCE_INACTIVE`, key `partnerId`, reported together with `warehouseId` when the
  warehouse is inactive too (005/R13: header masters together and alone) — and, after every activity check
  and before conversion, still has the role — else `409 PARTNER_ROLE_MISSING`, key `partnerId` (the order
  of 009/R12). A refused posting changes nothing; after the partner is reactivated or given the role back,
  posting succeeds.
- R13. A posted document is immutable (005/R11): its partner never changes. `partner` shows the partner's
  current code and name (005/R23). Deactivating the partner or taking the role away afterwards (004/R15
  permitting) leaves posted documents untouched.

Reversal
- R14. The reversing document carries the original's partner — the same `partner`, or `null` when the
  original has none (006/R13 extended). Nothing is checked: an original whose partner is inactive or has
  lost the role since is reversed all the same (006/AC-65). The reverse body has no `partnerId`: it is an
  unknown property (`400`).

Effects on the partner
- R15. A partner is **used** while a stock document — draft, posted or reversed, linked or not — names it:
  `DELETE /partners/{id}` -> `409 IN_USE`. It can be renamed, re-coded and deactivated, and its roles
  changed. Deleting the last draft that names it (and no order naming it) makes it unused again (005/R27).

Reading
- R16. `GET /stock-documents?partnerId=` filters by reference (002/R19): the documents whose partner is that
  partner, linked and unlinked, of every type and status; combined with the other filters by AND. A
  malformed value -> `400` with key `partnerId`; an id that is no partner of the tenant -> an empty list.
  Ordering and paging are unchanged.

Existing data
- R17. After the migration every linked stock document has its order's partner (R8 holds for all data, old
  and new) and every unlinked one has none *(default)*. Reversing a document posted before this spec
  follows R14.

## 7. Edge cases

- E1. A receipt naming a customer-only partner, an issue naming a supplier-only partner ->
  `PARTNER_ROLE_MISSING`; nothing is created.
- E2. A partner that is unknown **and** a warehouse that is inactive: `REFERENCE_NOT_FOUND` for `partnerId`
  alone (the not-found stage comes first, R7).
- E3. A draft receipt with partner P; P is deactivated: the draft is read, replaced keeping P, and deleted;
  replacing it with another inactive partner -> `REFERENCE_INACTIVE`; posting -> `REFERENCE_INACTIVE`.
- E4. A draft receipt with partner P; P loses `isSupplier` (keeps `isCustomer`): replace keeping P and
  posting -> `PARTNER_ROLE_MISSING`; replace with `partnerId: null` -> `200`, and then it posts.
- E5. A linked receipt created with `partnerId` of another supplier **and** for another warehouse -> one
  `ORDER_MISMATCH` with keys `partnerId` and `warehouseId`.
- E6. A linked draft replaced with `"partnerId": null` -> `200`, `partner` still the order's supplier.
- E7. The supplier of a confirmed order is deactivated, then a linked receipt is created and posted -> both
  succeed (R10); `partner` is that supplier.
- E8. `"partnerId": ""`, `"abc"`, `123`, `{}` -> `400` with key `partnerId`.
- E9. A transfer or count with `"partnerId": null` -> accepted; with a real partner id -> `400`.
- E10. A partner named only by one draft receipt: `DELETE` -> `IN_USE`; after the draft is deleted or
  replaced with `partnerId: null` -> `204`.

## 8. Tenant isolation

- T1. `PartnerId` is tenant-owned data on a tenant-owned row; the foreign key includes `TenantId`, so a
  stock document can never name another tenant's partner.
- T2. Another tenant's partner id as `partnerId` on an unlinked document -> `409 REFERENCE_NOT_FOUND`, the
  same answer as for a random id; on a linked document -> `ORDER_MISMATCH`, the same as for any other id.
- T3. `GET /stock-documents?partnerId=<another tenant's partner>` -> `200` with an empty list.
- T4. `IN_USE` of a partner is decided by the caller's tenant's documents only.

## 9. Security requirements

- S1. Any tenant key may set and remove the partner of a draft (restrictable from spec 018);
  `updatedBy` / `postedBy` attribute the act as before.
- S2. Who delivered what to whom is tenant data and can be personal data: partner ids, codes and names are
  not logged and never appear in another tenant's errors. Error bodies name the key `partnerId`, never the
  partner.
- S3. No input in `partnerId` may cause `500` (001/R16).

## 10. Acceptance criteria

Conventions and setup as in specs 009 §10, 010 §10 and 011 §10: the standard setup (unit `pcs`, stock
articles `A` and `B`, warehouses `W1` and `W2`), partners `SUP` (supplier only) and `CUS` (customer only),
"PO […]", "SO […]", "Receive n of A into W1", "Stock(A, W)".
- `SUP2` is a second supplier-only partner; `BOTH` a partner with both roles.
- "Receipt(P)" / "Issue(P)" means: create a stock document of that type for `W1`, dated `2026-10-10`, with
  one line `{ articleId: A, quantity: 1 }` and `"partnerId": P`.
Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. The **only** earlier tests changed:
  1. tests asserting the exact property set of a stock document or of its list summary, or the
     `outputSchema` of a tool returning one (they gain `partner`);
  2. tests pinning the exact input-schema properties of `stock_document_create`, `stock_document_update` or
     `stock_document_list` (they gain `partnerId`);
  3. `PUT /stock-documents/{id}` and `stock_document_update` requests for a **receipt or an issue** in
     earlier tests gain `"partnerId": null` and nothing else; tests that enumerate the properties a replace
     body must contain gain the case of AC-22;
  4. tests of the stock document's foreign keys or migrations kept by the builder.
  No test of spec 004 changes: an unused partner is still deleted with `204`. No test that asserts a
  quantity, a number, a posting result, an order's progress or the tool list changes. One migration added.
- AC-02 *(builder)* The migration (R17): on a database with a posted linked receipt, its reversing document,
  a posted delivery and an unlinked receipt made before it, afterwards the three linked documents have the
  order's supplier / customer and the unlinked one has none.
- AC-03 *(builder)* The partner of a document is resolved in Application, once, for HTTP and MCP; `Api`
  contains no rule about partners (architecture §2).

Inherited behaviour — smoke
- AC-10 `GET /stock-documents?partnerId=not-a-uuid` -> `400` with key `partnerId`; a create with an unknown
  header property next to `partnerId` -> `400` with that property's key.
- AC-11 `tools/list` returns 57 tools; `stock_document_create`, `stock_document_update` and
  `stock_document_list` have `partnerId` in their input properties and not in `required`.

Unlinked documents — create and read
- AC-20 Receipt(SUP) -> `201`; `partner` is `{ id, code, name }` of `SUP`; `GET` by id and the list summary
  show the same. Issue(CUS), after receiving stock, -> `201` with `partner` `CUS`.
- AC-21 A receipt created without `partnerId` and one with `"partnerId": null` -> `201`, `partner == null`;
  both post exactly as in spec 005.
- AC-22 Replace: a draft Receipt(SUP) replaced with `"partnerId": SUP2` -> `partner` `SUP2`; with
  `"partnerId": null` -> `partner == null`; a replace body **without** `partnerId` -> `400` with key
  `partnerId`, and the draft is unchanged.
- AC-23 Receipt(BOTH) and Issue(BOTH) -> `201`.
- AC-24 Wrong role, nothing created: Receipt(CUS) -> `409` `PARTNER_ROLE_MISSING` with key `partnerId`;
  Issue(SUP) -> the same. A draft Receipt(SUP) replaced with `CUS` -> the same, the draft unchanged.
- AC-25 References: a random uuid -> `409` `REFERENCE_NOT_FOUND` with key `partnerId`; an inactive supplier
  -> `409` `REFERENCE_INACTIVE` with key `partnerId`; `"abc"`, `123`, `""` -> `400` with key `partnerId`.
- AC-26 Not on other types: a transfer and a count created with `"partnerId": SUP` -> `400` with key
  `partnerId`; with `null` or without it -> `201` and `partner == null`; their replace without `partnerId`
  -> `200`.
- AC-27 A line with `"partnerId"` -> `400` (unknown property of the line).
- AC-28 Renaming `SUP` changes `partner.name` on a draft and on a posted document that names it.

Unlinked documents — posting and reversal
- AC-30 Post Receipt(SUP) of 5 A -> `200`, `partner` `SUP`; Stock(A, W1) rises by 5; the ledger entries have
  the properties and values of spec 005. A `PUT` afterwards -> `INVALID_STATE`; `partner` unchanged.
- AC-31 Inactive at posting: draft Receipt(SUP), deactivate `SUP`: `GET` -> `200`; a replace keeping `SUP`
  -> `200`; post -> `409` `REFERENCE_INACTIVE` with key `partnerId`, still a draft, no number consumed;
  reactivate -> post `200`.
- AC-32 Warehouse and partner both inactive at posting -> one `REFERENCE_INACTIVE` with keys `warehouseId`
  and `partnerId`.
- AC-33 Role lost: draft Receipt(BOTH); `BOTH` is replaced to customer only: post -> `409`
  `PARTNER_ROLE_MISSING` with key `partnerId`; a replace keeping it -> the same; a replace with
  `"partnerId": null` -> `200`, then post -> `200`.
- AC-34 Reversal copies the partner: reverse a posted Receipt(SUP) -> `201`, the reversing document has
  `partner` `SUP`; the reversed original keeps it. Reverse a posted receipt without a partner ->
  `partner == null`. `POST …/reverse` with `"partnerId"` in the body -> `400`.
- AC-35 Reversal checks nothing about the partner: post Receipt(SUP), deactivate `SUP` and (a second case)
  take a supplier role away from `BOTH` after posting Receipt(BOTH) -> both reversals `201` with the
  original's partner.

Linked documents
- AC-40 PO [(A, 10, 1)] (supplier `SUP`); a linked receipt created without `partnerId` -> `201`, `partner`
  `SUP`; with `"partnerId": null` -> the same; with `"partnerId": SUP` -> the same.
- AC-41 The mirror: SO [(A, 10, 1)] (customer `CUS`), stock received; a delivery created without `partnerId`
  -> `partner` `CUS`.
- AC-42 Cannot differ, nothing created: a linked receipt with `"partnerId": SUP2` -> `409` `ORDER_MISMATCH`
  with key `partnerId`; with a random uuid -> the same code and key; with `SUP2` and `W2` -> one
  `ORDER_MISMATCH` with keys `partnerId` and `warehouseId`.
- AC-43 Replace of a linked draft: with `"partnerId": null` -> `200`, `partner` still `SUP`; with `SUP2` ->
  `ORDER_MISMATCH` with key `partnerId`; without `partnerId` -> `400`.
- AC-44 The order's partner is not re-checked: after PO […] is confirmed, deactivate `SUP`; create a linked
  receipt -> `201` with `partner` `SUP`; post -> `200`; the order's received quantity rises as in spec 009.
- AC-45 Post and reverse a linked receipt: the posted and the reversing document both show `partner` `SUP`,
  `purchaseOrder` and `orderLineNo` as in spec 009; order progress is that of 009/R33.

List filter
- AC-50 With Receipt(SUP) (draft), a posted Receipt(SUP2), a posted linked receipt of a `SUP` order, a
  receipt without partner and an Issue(CUS): `GET /stock-documents?partnerId=SUP` -> exactly the two `SUP`
  documents; `?partnerId=CUS` -> the issue; `?partnerId=SUP&status=posted` -> the linked one;
  `?partnerId=SUP&type=issue` -> empty; `?partnerId=<random uuid>` -> `200`, empty.
- AC-51 `total`, `limit` and `offset` of the filtered list follow the list envelope (three `SUP` documents,
  `limit=2` -> two items, `total == 3`).

Partner in use
- AC-60 `DELETE /partners/{SUP2}` while a draft Receipt(SUP2) exists -> `409` `IN_USE`; after the draft is
  replaced with `"partnerId": null` -> `204`.
- AC-61 A partner named by a posted receipt -> `IN_USE` for good; it can still be renamed and deactivated
  (`200`). A partner named by nothing is deleted with `204` as before.

Tenant isolation
- AC-70 Tenant 2's supplier id as `partnerId` of tenant 1's receipt -> `409` `REFERENCE_NOT_FOUND` with key
  `partnerId`, a body equal in shape to that for a random uuid; on tenant 1's linked receipt ->
  `ORDER_MISMATCH`.
- AC-71 Tenant 1 lists `?partnerId=<tenant 2's supplier>` -> `200`, empty, although tenant 2 has receipts
  from it. Tenant 2's supplier named by tenant 2's receipts only is not `IN_USE` because of tenant 1.

MCP
- AC-80 `stock_document_create` with `partnerId` for a receipt -> the result has `partner`; for a transfer
  -> error `VALIDATION_FAILED` with key `partnerId`; with a customer-only partner on a receipt ->
  `PARTNER_ROLE_MISSING`.
- AC-81 `stock_document_update` of a draft receipt without `partnerId` -> `VALIDATION_FAILED` with key
  `partnerId`; with `null` -> `partner == null`.
- AC-82 `stock_document_list` with `partnerId` returns the same documents as the HTTP filter (parity);
  `stock_document_post` of a draft whose partner was deactivated -> `REFERENCE_INACTIVE`.
- AC-83 `partner_delete` of a partner named by a stock document -> `IN_USE`.

Unchanged
- AC-90 After the criteria above, "Balanced" (011 §10) holds for every article and warehouse used: the
  partner changes no quantity.

## 11. Notes for the tester and the builder

Tester
- The weight is on three rules: the role by type (AC-24, AC-33), "the order's partner and no other"
  (AC-40–AC-44), and the partner surviving posting and reversal unchanged (AC-30, AC-34, AC-35).
- The approved changes to earlier tests are exactly those of AC-01. Item 3 is mechanical — add
  `"partnerId": null` to the replace bodies of receipts and issues, in the helpers where they exist. If an
  earlier test fails for another reason, write it in `docs/questions/011a-q.md` before changing it.
- AC-02 and AC-03 are the builder's.

Builder
- One nullable column, one foreign key, one index, one migration with the backfill of R17 in plain SQL.
- Resolve the partner in the Application operation that saves a stock document: for a linked document take
  the order's partner and compare a non-null request value with it (R9, together with the warehouse
  comparison of 009/R21); for an unlinked one run the reference and role checks (R5–R7). The role check is
  the one orders use — share it, do not write a second.
- Keep "absent" and `null` distinct in the replace request (R3) and equal in the create request.
- Posting: add the partner to the header-masters activity check of 005/R13 for unlinked documents only, and
  the role check after it (R12). Nothing in the ledger writer, the balance writer or the lock changes.
- Reversal copies `PartnerId` with the other header fields (R14).
- `IN_USE` for the partner: extend the existing "used" check and the translation of the foreign-key
  violation.
- Anything unclear or contradictory: `docs/questions/011a-q.md`, then continue with the rest.

## 12. Architect's defaults for the owner

Each stands until the owner says otherwise; the rule that carries it is named.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | Must a manual receipt or issue name a partner? | No — optional; without one it behaves as before | R2 |
| 2 | Which partner may it be? | A receipt: a supplier; an issue: a customer. Returns (the reverse pairing) are a later spec | R6 |
| 3 | A receipt or delivery linked to an order | Always the order's partner; naming another is refused, not silently corrected | R8, R9 |
| 4 | Is the partner checked again when a manual document is posted? | Yes — it must be active and still have the role. On a linked document it is not checked (as in spec 009) | R10, R12 |
| 5 | Reversal | The reversing document carries the original's partner; nothing is checked | R14 |
| 6 | Documents that already exist | Linked ones get their order's partner; manual ones stay without | R17 |
| 7 | Must a replace of a draft send `partnerId`? | Yes for a receipt or issue (`null` removes it), so that a partner is never dropped by omission; this is why earlier replace requests gain `"partnerId": null` | R3 |
| 8 | Transfers and counts | No partner | R1 |
