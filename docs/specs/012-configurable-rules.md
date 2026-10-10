# Spec 012 — Configurable rules: the mechanism and the stock and order policies

Status: ready for the tester and the builder. Owner requirement of 2026-10-10: every validation and business
rule is configurable per tenant; we ship the default (`CLAUDE.md`, ADR-0020, architecture §11).
Branches: `tests/012-configurable-rules` (tester), `feat/012-configurable-rules` (builder).
Read first: **ADR-0020** (the mechanism — this spec is its contract), `docs/rules.md` (which rules and why),
specs **005** and 006 (posting, negative stock, reversal), **009** and **010** (order quantities,
reservation), 011 (stored balance, the per-tenant lock), 008 (count).
Why this spec and why this number: `docs/roadmap.md` section 3, row 12.

**Inherited, not re-specified** (roadmap §4): everything specs 001–011a established — transport, errors,
references, MCP mapping, documents, posting, reversal, orders, balances. This spec states only what is new or
changed. Numbers are local; "009/R26" means rule R26 of spec 009.

**The one sentence that governs everything below: a tenant that sets no rule behaves exactly as specs
001–011a say.** Every default is today's behaviour.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

A tenant — a person or an agent — can see every configurable rule of the system with its meaning, its
default and its current value, change it, put it back, and see who changed what. Six rules that until now
were fixed are the first on it:

- may stock go below zero (`stock.negativeStock`);
- how much more than ordered may be received (`purchase.overReceiptPercent`) or delivered
  (`sales.overDeliveryPercent`);
- does reserved stock block other movements (`sales.reservation`);
- how many decimals a quantity may have (`quantity.decimals`);
- how many lines a document may have (`document.maxLines`).

When one of them refuses an operation, the error says which.

## 2. Scope

In scope
1. The rule registry with rule types `choice`, `integer` and `decimal`; values per tenant; history of changes.
2. Five operations over HTTP and five MCP tools: list, get, set, reset, list changes.
3. The six rules of section 6.1, read by the existing checks instead of their constants.
4. The `rules` member of the error document and of the MCP tool error; one new error code,
   `STOCK_RESERVED`.
5. Removal of the check constraint `StockBalance.Quantity >= 0`.

Out of scope
- Every other rule of `docs/rules.md` §3 (batches 013, 014, number series, permissions). They stay fixed
  checks with their default's behaviour until their spec.
- Rule types `boolean`, `text` and `pattern`: each arrives with the first rule that uses it (013).
- Who may change rules: every tenant key, until permissions (roadmap 021). *(default)*
- Values per warehouse, article, partner or key; tenant-written rules; effective dates (ADR-0020, "What it
  cannot do").
- Allocation of stock to a particular order; refusing the confirmation of an order (`docs/rules.md`, 014).
- Cost of negative stock (valuation, roadmap 015).

## 3. Data

`RuleValue` (tenant-owned): `TenantId`, `Key` (text, max 100), `Value` (JSON), `UpdatedAt`, `UpdatedBy`.
Primary key `(TenantId, Key)`. A row exists only for a rule the tenant has set; reset removes it.

`RuleChange` (tenant-owned, append-only): `Id` uuid v7, `TenantId`, `Key`, `Action` (`set` | `reset`),
`OldValue`, `NewValue` (JSON, the effective values before and after), `ChangedAt`, `ChangedBy`.

`UpdatedBy` and `ChangedBy` are foreign keys `(TenantId, …)` -> `ApiKey (TenantId, Id)`, `ON DELETE RESTRICT`
(architecture §8). No foreign key on `Key`: the registry is code.

`StockBalance` loses its check constraint `Quantity >= 0` (011 §3).

One migration. It adds the two tables, empty, and drops the constraint. No existing row changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key required. Bodies, query strings and errors follow specs 001–003.

| Operation | Request | Success |
|---|---|---|
| List rules | `GET /rules?search=&group=&source=&limit=&offset=` | `200` list envelope of rules |
| Get rule | `GET /rules/{key}` | `200` rule |
| Set rule | `PUT /rules/{key}` with body `{ "value": <JSON> }` | `200` rule |
| Reset rule | `POST /rules/{key}/reset`, no body | `200` rule |
| List changes | `GET /rule-changes?key=&limit=&offset=` | `200` list envelope of changes |

Rule (every property always present):

```json
{ "key": "stock.negativeStock", "group": "stock", "name": "Negative stock",
  "description": "Whether a posting or reversal may take stock on hand below zero. …",
  "type": "choice", "allowed": { "values": ["refuse", "allow"] },
  "default": "refuse", "value": "refuse", "source": "default",
  "updatedAt": null, "updatedBy": null }
```

- `type`: `"choice"`, `"integer"` or `"decimal"`.
- `allowed`: for `choice` `{ "values": [strings] }`; for `integer` `{ "min", "max" }`; for `decimal`
  `{ "min", "max", "decimals" }`.
- `value` is the value in force for the tenant; `source` is `"tenant"` when the tenant has set it and
  `"default"` otherwise (then `value == default`).
- `updatedAt` / `updatedBy` (API key id) are those of the tenant's last set or reset of this rule; `null`
  while the tenant has never changed it.
- `name` and `description` are English text for a reader; they are not contract and tests do not pin them.

Change: `{ "id", "key", "action": "set" | "reset", "oldValue", "newValue", "changedAt", "changedBy" }`.

Errors:

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 400 | `VALIDATION_FAILED` | Body malformed, unknown property, `value` missing; value of the wrong JSON type or outside `allowed`; a bad query parameter | `body`, the property, `value`, the parameter |
| 404 | `NOT_FOUND` | `{key}` is not a rule of the registry (any string) | — |

New error code (registry: architecture §6):

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `STOCK_RESERVED` | With `sales.reservation` = `block`: the posting or reversal would take goods that confirmed sales orders reserve (R27). | `lines[i].quantity` |

**The `rules` member** (architecture §6, ADR-0020 decision 9). A refusal caused by one of the six rules adds
to the problem document:

```json
"rules": [ { "key": "stock.negativeStock", "value": "refuse", "fields": ["lines[0].quantity"] } ]
```

one element per rule that refused, `value` the value it had, `fields` the `errors` keys it produced (in the
order they appear in `errors`). The member is absent when none of the six refused.

## 5. Operations — MCP

Five new tools; `tools/list` returns exactly 62: the 57 of spec 011 and `rule_list`, `rule_get`, `rule_set`,
`rule_reset`, `rule_change_list`.

| Tool | Arguments | Result |
|---|---|---|
| `rule_list` | `search?`, `group?`, `source?`, `limit?`, `offset?` | as `GET /rules` |
| `rule_get` | `key` | the rule |
| `rule_set` | `key`, `value` (boolean, number, string or null — judged by the rule's type) | the rule |
| `rule_reset` | `key` | the rule |
| `rule_change_list` | `key?`, `limit?`, `offset?` | as `GET /rule-changes` |

- Mapping, attribution and errors as 003/R12–R17. `key` is the addressing argument: missing, `null` or not a
  string -> `VALIDATION_FAILED` with key `key`; a string that is no rule -> `NOT_FOUND`.
- A tool error carries `rules` exactly as the HTTP problem does: `{ "code", "detail", "errors"?, "rules"? }`.
- Annotations: `rule_list`, `rule_get`, `rule_change_list` read-only; `rule_set`, `rule_reset` not read-only,
  idempotent, not destructive.
- Descriptions say:
  - `rule_list` — these are the business rules this company can change; each has a default; read
    `description` and `allowed` before setting one.
  - `rule_set` — changes the rule for the whole company and for every user and agent, from the next
    operation on; existing data is not changed; the change is recorded with your key. Do not change a rule
    only to get one refused operation through unless the user asked for the rule to change.
  - `rule_reset` — returns the rule to its default.
  - `stock_document_post`, `stock_document_reverse` — name the rules that can refuse (`stock.negativeStock`,
    `sales.reservation`, and for a linked document the order tolerance rule) and add `STOCK_RESERVED` to
    their error codes; say that a refusal's `rules` names the rule.
  - `stock_document_create` / `_update`, `purchase_order_create` / `_update`, `sales_order_create` /
    `_update` — say that the number of lines and the decimals of `quantity` are limited by
    `document.maxLines` and `quantity.decimals`.

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

| Key | Type | Default | Allowed | Replaces the fixed check |
|---|---|---|---|---|
| `stock.negativeStock` | choice | `refuse` | `refuse`, `allow` | 005/R15–R16, 006/R7, R16, 008/R18, 010/R9 |
| `purchase.overReceiptPercent` | decimal | `0` | `min` 0, `max` 1000, `decimals` 2 | 009/R26 |
| `sales.overDeliveryPercent` | decimal | `0` | `min` 0, `max` 1000, `decimals` 2 | 010/R7 (009/R26 mirrored) |
| `sales.reservation` | choice | `inform` | `inform`, `block` | 010/R17 |
| `quantity.decimals` | integer | `6` | `min` 0, `max` 6 | 005/R6 ("at most 6 decimal places") |
| `document.maxLines` | integer | `200` | `min` 1, `max` 1000 | 005/R4, 008/R2, 009/R5 ("1 to 200") |

Checks this spec adds that are **invariants** (`docs/rules.md` §2, I12 — the mechanism itself): a value must
be of the rule's type and within `allowed` (R6); a key must be a rule of the registry (R5); a change is
attributed and kept (R10–R11). The ends of the allowed ranges are platform bounds (I10): 6 decimals of
storage; 1000 lines within the 1 MB body.

### 6.2 The registry and its operations

- R1. The registry of this spec has exactly the six rules of 6.1. `GET /rules` lists all of them for every
  tenant, set or not; `total` is 6 without filters.
- R2. List: ordered by `key`, ascending, ordinal. `group` filters by exact value (a group that no rule has ->
  empty list); `source` accepts exactly `default` or `tenant`; `search` as 001/R9, matching `key` or `name`.
  Filters combine with AND; `limit` / `offset` as everywhere.
- R3. A tenant that has set nothing: every rule has `value == default`, `source == "default"`,
  `updatedAt == null`, `updatedBy == null`. Creating a tenant writes no rule value.
- R4. `{key}` addresses the rule and is compared exactly (case-sensitive). Anything that is not one of the
  registry's keys — another case, an empty or over-long string, a key of a later batch — is `404 NOT_FOUND`.
- R5. **Set.** Order of checks: form of the body (`400`: malformed, unknown property, `value` missing) ->
  the rule exists (`404`) -> the value (`400`, key `value`). On success the tenant has that value:
  `source == "tenant"`, `updatedAt` = now, `updatedBy` = the acting key.
- R6. **A value is valid** when it has the JSON type of the rule's type and lies within `allowed`:
  - `choice`: a string equal to one of `values`, exactly (`"Refuse"` is invalid);
  - `integer`: a JSON number without a fractional part (`6` and `6.0` are the same value), `min` ≤ v ≤ `max`;
  - `decimal`: a JSON number with at most `decimals` decimal places, `min` ≤ v ≤ `max`;
  - `null`, a quoted number, a boolean, an array or an object is the wrong type.
  Otherwise `400` with key `value`; nothing changes.
- R7. Setting a value equal to the default is a set like any other: the tenant then has its own value
  (`source == "tenant"`), which no later change of our default touches (ADR-0020, decision 7).
- R8. Setting the value the tenant already has (`source == "tenant"`, same value) succeeds and changes
  nothing: no change is recorded, `updatedAt` / `updatedBy` stay.
- R9. **Reset** removes the tenant's value: `value == default`, `source == "default"`, `updatedAt` /
  `updatedBy` those of the reset. Reset of a rule whose `source` is already `"default"` succeeds and changes
  nothing.
- R10. **Every set and reset that changes something records one change**: `action`, the effective value
  before (`oldValue`) and after (`newValue`), `changedAt` (equal to the rule's `updatedAt`), `changedBy`.
  Changes are never altered or deleted; no operation removes them.
- R11. `GET /rule-changes`: newest first (`changedAt` descending, then `id` descending). `key` filters by
  exact key; a string that is no rule gives an empty list.
- R12. No rule's validity depends on another rule's value; any of the six can be set and reset in any order.

### 6.3 When a changed rule applies

- R13. **From the next operation.** An operation that starts after a set or reset has been answered is
  judged by the new value. There is no delay and no cache to wait for.
- R14. **One operation, one set of rules.** Under parallel requests every operation is judged entirely by
  the values before a change or entirely by the values after it. Set and reset take the per-tenant lock
  (ADR-0012 amendment); every operation that holds that lock reads the rules while holding it.
- R15. **Nothing stored is touched by a change.** No document, line, ledger entry, balance, order quantity or
  number changes, is re-checked or is refused later *for having been written* under another value. Reading
  never applies a rule.
- R16. Which operation applies which rule:
  - `quantity.decimals` and `document.maxLines` judge the request body of **create and replace** of a stock
    document, a purchase order and a sales order — nothing else. Posting, confirmation and reversal do not
    look at them: a draft saved under a wider value posts as it is.
  - `stock.negativeStock` and `sales.reservation` judge **posting and reversal** of stock documents.
  - `purchase.overReceiptPercent` and `sales.overDeliveryPercent` judge **posting** of a linked receipt /
    delivery.

### 6.4 `stock.negativeStock`

- R17. For a posting or a reversal, and for every (article, warehouse) pair it writes entries for, let Δ be
  the sum of those entries and Q the pair's stock on hand before. With `refuse`, the operation is refused
  with `409 INSUFFICIENT_STOCK` when for any pair **Δ < 0 and Q + Δ < 0**. Keys and everything else as
  005/R15, 006/R7 and R16, 008/R18, 010/R9. While no pair is negative this is exactly the rule as built.
- R18. With `allow` there is no such check: `INSUFFICIENT_STOCK` is never answered. Stock on hand, the
  stored balance and `availableQuantity` may be negative. Everything else about posting is unchanged —
  references, conversion, order checks, numbering, atomicity.
- R19. Invariants that hold for either value (`docs/rules.md` I5): stock on hand == sum of the ledger ==
  stored balance; verify (`GET /stock-balance-differences`) is empty and rebuild corrects nothing, also with
  negative pairs.
- R20. Reading with negative stock: stock on hand lists a pair whose `quantity` is not zero, negative
  included (005/R20). In the stock list of a warehouse `hasStock=true` keeps `quantity > 0` and
  `hasStock=false` keeps `quantity <= 0`. A count's book quantity may be negative; its difference is counted
  − book (008/R7), and posting it brings the pair to the counted quantity (008/R12).
- R21. After `allow` is changed back to `refuse`, negative pairs stay as they are (R15). By R17 a posting
  that raises such a pair is accepted even if it stays negative; one that lowers it is refused.

### 6.5 `purchase.overReceiptPercent` and `sales.overDeliveryPercent`

Stated for purchasing with p = `purchase.overReceiptPercent`; it holds mirrored for deliveries against sales
orders with `sales.overDeliveryPercent`.

- R22. The **limit** of an order line is `baseQuantity × (1 + p / 100)`, rounded to 6 decimal places, half
  away from zero. Posting a linked receipt is refused with `409 QUANTITY_EXCEEDS_ORDER` when, for any order
  line, `receivedBaseQuantity` before the posting plus the document's `baseQuantity` for that line exceeds
  the limit. Keys, atomicity and place in the order of checks as 009/R26. With p = 0 this is 009/R26.
- R23. `receivedBaseQuantity` is still the sum over posted, non-reversing documents (009/R25) and may now
  exceed `baseQuantity`. `outstandingBaseQuantity` is `max(0, baseQuantity − receivedBaseQuantity)` while
  the order is `confirmed` (otherwise 0): never negative. `receiptStatus` is `"full"` when every line has
  `receivedBaseQuantity >= baseQuantity`. `incomingQuantity` (009/R35) and `reservedQuantity` (010/R15) sum
  the outstanding quantities and are therefore never lowered by an excess.
- R24. The value at posting decides. Lowering p afterwards changes no received quantity (R15); a further
  receipt is judged against the new limit. A reversal is not judged by this rule (009/R34).
- R25. A delivery is still judged for stock (R17) and reservation (R27) after the order's quantities
  (010/R7): the tolerance allows more than ordered, not more than there is.

### 6.6 `sales.reservation`

- R26. With `inform`, reserved quantity blocks nothing (010/R17, as built).
- R27. With `block`: for a posting or a reversal, and for every pair with Δ < 0 (R17), let
  `A_before` = `availableQuantity` of the pair before the operation and `A_after` the value it would have
  after it — quantity and reserved quantity both as they would then be. The operation is refused with
  `409 STOCK_RESERVED` when for any pair **the reserved quantity after it is above 0, `A_after < 0` and
  `A_after < A_before`**: something is still reserved, stock no longer covers it, and this operation made
  that worse. `errors` keys are
  `lines[i].quantity` for every line of the document with the article of such a pair (for a reversal: the
  original's lines, as 006/R16).
- R28. What follows from R27:
  - an unlinked issue and a transfer out of a warehouse cannot take stock below what confirmed sales orders
    for that warehouse still await;
  - a delivery against a sales order, up to the order line's outstanding quantity, lowers `quantity` and
    `reservedQuantity` alike, leaves `availableQuantity` unchanged, and is therefore **never** refused by
    this rule — whichever order it is for. Between orders the first delivery posted gets the goods; stock
    is not allocated to an order (out of scope);
  - the part of a delivery above the outstanding quantity (R22) lowers availability and is judged;
  - reversing a receipt, a transfer (at its destination) or a count that added stock lowers a pair and is
    judged; reversing an issue or a delivery never is.
- R29. **Posting a count is never judged by this rule** (nor by R17; 008/R13): a count states what is
  physically there. *(default)*
- R30. Order of checks on posting, extending 010/R7: … -> the order is `confirmed` -> the order's quantities
  (`QUANTITY_EXCEEDS_ORDER`) -> stock (`INSUFFICIENT_STOCK`) -> reservation (`STOCK_RESERVED`). On reversal:
  … -> stock -> reservation. Each stage answers alone. The two rules are independent: with
  `stock.negativeStock` = `allow` and `sales.reservation` = `block`, R27 still applies.
- R31. Confirming, closing and reopening a sales order are not affected by this rule (010/R4): availability
  can still become negative by confirming orders; R27 then refuses whatever would lower it further.

### 6.7 `quantity.decimals` and `document.maxLines`

- R32. With `quantity.decimals` = d, a line's `quantity` on create or replace of a stock document (all four
  types), a purchase order or a sales order may have at most d decimal places; otherwise `400` with key
  `lines[i].quantity`. Decimal places are counted numerically: `1.50` has one. With d = 6 this is 005/R6.
  All other bounds of a quantity (sign, maximum, JSON number) are unchanged and are not this rule's.
- R33. The rule concerns the entered `quantity` only. `factor`, `unitPrice`, `baseQuantity` and every stored
  or computed quantity keep six decimals (their rules: `docs/rules.md`, 014). A count's `bookQuantity` and
  `differenceQuantity` are computed and not judged.
- R34. With `document.maxLines` = n, `lines` on create or replace of a stock document, a purchase order or a
  sales order may have at most n elements; otherwise `400` with key `lines`. With n = 200 this is 005/R4.
  An empty or missing `lines` is refused as before, by an invariant (no `rules` member).

### 6.8 Errors name the rule

- R35. Every refusal by R17, R22, R27, R32 or R34 carries `rules` with that rule's key, the value in force
  and, as `fields`, exactly the `errors` keys that rule produced:

  | Refusal | `rules[].key` |
  |---|---|
  | `INSUFFICIENT_STOCK` | `stock.negativeStock` |
  | `QUANTITY_EXCEEDS_ORDER` on a receipt / on a delivery | `purchase.overReceiptPercent` / `sales.overDeliveryPercent` |
  | `STOCK_RESERVED` | `sales.reservation` |
  | `VALIDATION_FAILED`, a quantity with too many decimals | `quantity.decimals` |
  | `VALIDATION_FAILED`, too many lines | `document.maxLines` |

- R36. A `VALIDATION_FAILED` reports all invalid fields together as before. `rules` has one element per rule
  that contributed, ordered by key; fields that an invariant refused (a negative quantity, an unknown
  property) are in `errors` and in no element's `fields`. Whether individual lines are still judged when
  there are too many of them stays as built. A response in which no rule refused has no `rules`
  member — not an empty array.
- R37. Error codes, statuses and `errors` keys of specs 001–011a are unchanged.

## 7. Edge cases

- E1. `PUT /rules/stock.negativeStock` with `{}` -> `400`, key `value`; with `{ "value": "allow", "x": 1 }`
  -> `400`, key `x`; with `{ "value": "Allow" }`, `"yes"`, `true`, `null`, `1` -> `400`, key `value`.
- E2. `quantity.decimals`: `7`, `-1`, `2.5`, `"2"` -> `400`; `0`, `6`, `2.0` -> accepted (`2.0` is returned
  as the number 2).
- E3. `purchase.overReceiptPercent`: `-1`, `1000.01`, `2.345`, `"10"` -> `400`; `0`, `10`, `12.5`, `1000` ->
  accepted.
- E4. `PUT /rules/nope`, `PUT /rules/Stock.NegativeStock`, `PUT /rules/partner.role.required` (a later
  batch) with a valid body -> `404`; with `{}` -> `400` (form first, R5).
- E5. `DELETE /rules/{key}`, `POST /rules` -> `404` (001/E11). `GET /rules?foo=1` -> `400`, key `foo`;
  `?source=mine` -> `400`, key `source`; `?group=nope` -> `200`, empty.
- E6. Set, reset, then set the same value again -> three changes in the history. Setting the same value
  twice in a row -> one (R8).
- E7. `quantity.decimals` = 2 and a line `quantity: 1.005` -> `400`; `1.00`, `1.5`, `3` -> accepted. With
  `0`: `2` and `2.0` accepted, `0.5` refused.
- E8. `quantity.decimals` = 0 and a line in boxes of an article with factor 0.5: `quantity: 3` is accepted
  and its `baseQuantity` is `1.5` (R33).
- E9. `quantity.decimals` = 2 and a body with an invalid `documentDate`, a line `quantity: 1.005` and a
  line `quantity: -1` -> `400` with the three keys; `rules` has one element, `quantity.decimals`, with
  `fields` naming the first of the two lines only (R36).
- E10. `document.maxLines` = 1000 and a document of 1000 lines -> accepted (it is far below 1 MB).
- E11. `purchase.overReceiptPercent` = 10, order line of 3 boxes of 12 (base 36): limit 39.6; receipts of 36
  then 3.6 post; a further 0.000001 is refused. Rounding: a tolerance of 12.5 on a base quantity of
  0.000004 gives 0.0000045, so the limit is 0.000005.
- E12. Tolerance 10, 39 of 36 received, then the tolerance is set to 0: the order line still shows 39,
  outstanding 0, status `full`; any further receipt for it is refused; reversing the receipt works.
- E13. `block`: stock 10, a confirmed sales order for 8 (available 2). Unlinked issue of 2 -> posted
  (available 0); of 3 -> `STOCK_RESERVED`. Delivery of 8 against the order -> posted.
- E14. `block`: stock 10, orders SO1 for 10 and SO2 for 10 confirmed (available −10). Delivery of 10 for SO2
  -> posted; delivery for SO1 -> `INSUFFICIENT_STOCK` (not `STOCK_RESERVED`: stock comes first, R30).
- E15. `block`: available is −5 because orders were confirmed beyond stock; a receipt posts; an unlinked
  issue of any quantity is refused (it lowers availability further); a count that finds less posts (R29).
- E16. `allow`: an issue of 5 from an empty pair -> posted; stock on hand lists the pair with `-5`; verify
  is empty. A transfer of 5 out of an empty warehouse -> posted: −5 there, +5 at the destination.
- E17. `refuse` again with a pair at −5: receipt of 3 -> posted (−2); issue of 1 -> refused; reversal of
  the receipt of 3 -> refused; a count of 0 -> posts, the pair is 0.
- E18. A rule set by a key that is revoked afterwards keeps its value; `updatedBy` still names that key.

## 8. Tenant isolation

- T1. `RuleValue` and `RuleChange` are tenant-owned: `TenantId`, query filter, tenant-inclusive keys. The
  registry (definitions and defaults) is code and the same for all tenants; it contains no tenant data.
- T2. A value set by tenant X changes nothing for tenant Y: Y's `GET /rules` shows its own values, Y's
  operations are judged by Y's values, Y's `GET /rule-changes` shows only Y's changes.
- T3. `rules` in an error names the caller's tenant's value only.
- T4. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. All five operations require a tenant key (`401` / `403` as 001/S1–S4). Every tenant key, human or
  agent, may read and change rules *(default; restrictable from roadmap 021)*.
- S2. Every change is attributable: the rule shows the last key and time, the history every change (R10).
  No operation edits or deletes history.
- S3. Rule values are not secrets, but they are the tenant's: they appear only in that tenant's responses.
  Logs may record rule key, value, API key id and tenant id.
- S4. No value can be stored that the checks cannot handle: validation (R6) is the only way in, and a value
  read from storage that is no longer valid for its definition is treated as absent (the default applies) —
  it never causes `500`.
- S5. No input in `{key}` or `value` may cause `500` (001/R16).

## 10. Acceptance criteria

Conventions and setup as in specs 009 §10, 010 §10 and 011 §10: the standard setup (unit `pcs`, stock
articles `A` and `B`, warehouses `W1` and `W2`), unit `box` with A / box = 12, partners `SUP` and `CUS`,
"PO […]", "SO […]", "Receive n of A into W1", "Receive (k, n) against the order", "Deliver (k, n) against
the order", "Stock(A, W)", "Available(A, W)", "Balanced". In addition:
- "Set(key, v)" means `PUT /rules/{key}` with `{ "value": v }` answered `200`.
- "Issue n of A from W1" means: create an unlinked issue with one line and post it.
- "refused by (CODE, key, v)" means: the response has that `code`, a non-empty `errors`, and `rules` with
  exactly one element whose `key` and `value` are those and whose `fields` equal the keys of `errors`.
Each test uses its own tenant, so rules never leak between tests. Unmarked criteria are black-box (tester).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. **No test of specs 001–011a that
  asserts a status, a code, an `errors` key, a quantity, a number or a state changes** — they run in tenants
  that set no rule (AC-40). The **only** earlier tests changed:
  1. the literal tool list and its count (57 -> 62);
  2. tests asserting the **exact member set** of a problem document or tool error for `INSUFFICIENT_STOCK`,
     `QUANTITY_EXCEEDS_ORDER`, or `VALIDATION_FAILED` caused by a quantity of more than 6 decimals or by
     more than 200 lines (they gain `rules`);
  3. tests pinning the error-code list in the description of `stock_document_post` or
     `stock_document_reverse` (they gain `STOCK_RESERVED`);
  4. the builder's test that the database refuses a negative `StockBalance.Quantity` (011): removed; AC-52
     takes its place.
  One migration added.
- AC-02 *(builder, model)* The model and table tests pass with `RuleValue` and `RuleChange`; both are
  tenant-owned and filtered.
- AC-03 *(builder, unit)* Value validation (R6) for the three types, including the edge values of E1–E3;
  the limit of R22 with its rounding; R17 and R27 as pure functions of Δ, Q, reserved and the rule value.
- AC-04 *(manual)* The six constants are gone from the checks: each of the six behaviours is decided by a
  value obtained through `IRules` and passed to a Domain function; `Api` reads no rule (architecture §11).
- AC-05 *(builder)* A `RuleValue` row whose stored value is invalid for its definition (written directly) is
  shown and applied as the default (S4).
- AC-06 *(builder)* Migration: on a database at the previous migration with posted documents, afterwards
  both tables exist and are empty, `StockBalance` has no check constraint on `Quantity`, and Balanced holds.

Inherited behaviour — smoke
- AC-10 Each of the five routes without a credential -> `401`; with the admin key -> `403`.
- AC-11 `GET /rules?foo=1` -> `400`, key `foo`; `PUT /rules/stock.negativeStock` with an unknown property ->
  `400` with that property's key; `GET /rules?limit=0` -> `400`, key `limit`.
- AC-12 `tools/list` returns exactly 62 names (literal list).

The registry
- AC-20 In a new tenant `GET /rules` -> `200`, `total == 6`, keys in this order with these values
  (literal — the inventory of rules): `document.maxLines` 200, `purchase.overReceiptPercent` 0,
  `quantity.decimals` 6, `sales.overDeliveryPercent` 0, `sales.reservation` `"inform"`,
  `stock.negativeStock` `"refuse"`. For each: `value == default`, `source == "default"`,
  `updatedAt == null`, `updatedBy == null`, `type` and `allowed` as in 6.1, and non-empty `name` and
  `description`.
- AC-21 `GET /rules/sales.reservation` -> the same object as in the list. `GET /rules/nope`,
  `/rules/Sales.Reservation` -> `404`.
- AC-22 Filters: `group=sales` -> the two `sales.` rules; `source=tenant` -> empty in a new tenant, and
  after one Set exactly that rule; `search=percent` -> the two percent rules; `group=nope` -> empty.

Set, reset, history
- AC-30 Set(`stock.negativeStock`, `"allow"`) -> `200` with `value == "allow"`, `source == "tenant"`,
  `default == "refuse"`, `updatedBy` = the acting key's id, `updatedAt` set; `GET` shows the same.
- AC-31 Invalid values (E1–E3) -> `400` with key `value`; the rule is unchanged and no change is recorded.
- AC-32 Order of checks (E4): unknown key with `{}` -> `400`; unknown key with a valid body -> `404`.
- AC-33 Setting the default's own value: Set(`quantity.decimals`, 6) in a new tenant -> `source ==
  "tenant"`, `value == 6`; one change with `oldValue == 6`, `newValue == 6`.
- AC-34 Setting the same value twice: the second answers `200` with the same `updatedAt`; one change.
- AC-35 `POST /rules/stock.negativeStock/reset` after AC-30 -> `value == "refuse"`, `source == "default"`,
  `updatedAt` later than before, `updatedBy` the acting key. A second reset -> `200`, nothing changes.
  Reset of an unknown key -> `404`.
- AC-36 History: after Set to `"allow"` by key K1 and reset by key K2 (an `agent` key),
  `GET /rule-changes?key=stock.negativeStock` -> two items, newest first: `{ action: "reset", oldValue:
  "allow", newValue: "refuse", changedBy: K2 }`, `{ action: "set", oldValue: "refuse", newValue: "allow",
  changedBy: K1 }`; `changedAt` of each equals the rule's `updatedAt` after that change. Without `key` the
  list has the changes of all rules; `key=nope` -> empty.
- AC-37 Every rule accepts the ends of its range and its default: for each of the six, Set to `min` and
  `max` (or to each of `values`) -> `200`.

Defaults reproduce specs 001–011a
- AC-40 In a tenant that sets nothing: an issue above stock -> `INSUFFICIENT_STOCK`; a receipt above its
  order -> `QUANTITY_EXCEEDS_ORDER`; a delivery above its order -> `QUANTITY_EXCEEDS_ORDER`; an unlinked
  issue of goods a confirmed order reserves -> posted; a quantity `1.0000001` -> `400`; 201 lines -> `400`;
  `1.000001` and 200 lines -> accepted.
- AC-41 The same in a tenant in which each of the six rules was Set to another value and then reset.
- AC-42 **Errors name the rule**: the five refusals of AC-40 are "refused by" (`INSUFFICIENT_STOCK`,
  `stock.negativeStock`, `"refuse"`), (`QUANTITY_EXCEEDS_ORDER`, `purchase.overReceiptPercent`, 0),
  (`QUANTITY_EXCEEDS_ORDER`, `sales.overDeliveryPercent`, 0), (`VALIDATION_FAILED`, `quantity.decimals`, 6),
  (`VALIDATION_FAILED`, `document.maxLines`, 200).
- AC-43 A refusal no rule caused has no `rules` member: `VALIDATION_FAILED` for a negative quantity, for an
  empty `lines`, for an unknown property; `REFERENCE_NOT_FOUND`; `INVALID_STATE`; `ORDER_NOT_OPEN`;
  `COUNT_OUTDATED`.
- AC-44 Mixed (E9): the response has the three `errors` keys, and `rules` has exactly one element,
  `quantity.decimals`, whose `fields` is the one line with too many decimals.

`stock.negativeStock`
- AC-50 Set to `"allow"`. Issue 5 of A from W1 (empty) -> posted; Stock(A, W1) == −5; the pair is listed by
  `GET /stock-on-hand` with `quantity == -5` and `availableQuantity == -5`; Balanced;
  `GET /stock-balance-differences` -> `total == 0`.
- AC-51 `allow`: a transfer of 5 A from W1 (empty) to W2 -> posted; Stock(A, W1) == −5, Stock(A, W2) == 5.
  Reversing a receipt whose goods were issued since -> `201`. A delivery above stock (within its order) ->
  posted.
- AC-52 `allow`, with negative pairs: `POST /stock-balances/rebuild` -> `corrected == 0`; verify empty; the
  stock list of W1 with `hasStock=false` contains A, with `hasStock=true` does not.
- AC-53 `allow`: a count of A in W1 at −5 shows `bookQuantity == -5`; counted 2 -> `differenceQuantity == 7`;
  posted; Stock(A, W1) == 2.
- AC-54 Back to `refuse` (E17), pair at −5: Receive 3 -> posted, −2; Issue 1 -> refused by
  (`INSUFFICIENT_STOCK`, `stock.negativeStock`, `"refuse"`); reversing the receipt of 3 ->
  `INSUFFICIENT_STOCK`; nothing stored changed when the rule changed (the documents, the ledger and the −5
  read the same before and after the Set); Balanced throughout.

Over-receipt and over-delivery
- AC-60 Set(`purchase.overReceiptPercent`, 10). PO [A, 3 box, price 1] (base 36). Receive (1, 36) -> posted;
  Receive (1, 3.6) -> posted; Received(1) == 39.6, Outstanding(1) == 0, `receiptStatus == "full"`,
  Incoming(A, W1) == 0. Receive (1, 0.000001) -> refused by (`QUANTITY_EXCEEDS_ORDER`,
  `purchase.overReceiptPercent`, 10).
- AC-61 One receipt of 40 against a fresh order as in AC-60 -> refused (nothing posted, no number consumed);
  of 39.6 -> posted.
- AC-62 Lowering the tolerance (E12): after AC-60, Set to 0 -> the order still shows Received(1) == 39.6 and
  `full`; a further receipt of 1 is refused with value 0 in `rules`; reversing the receipt of 3.6 -> `201`,
  Received(1) == 36.
- AC-63 Partial then excess: tolerance 10, order line of base 100; Receive 50 -> Outstanding 50,
  `partial`; Receive 60 -> posted, Received 110, Outstanding 0; Receive 1 -> refused.
- AC-64 Mirrored for sales with `sales.overDeliveryPercent` = 10 and enough stock: SO [A, 100]; Deliver 110
  -> posted; `deliveredBaseQuantity == 110`, outstanding 0, `deliveryStatus == "full"`,
  `reservedQuantity` of the pair 0. Deliver 1 more -> refused by (`QUANTITY_EXCEEDS_ORDER`,
  `sales.overDeliveryPercent`, 10).
- AC-65 The two tolerances are separate: with only `purchase.overReceiptPercent` = 10, a delivery above its
  sales order is still refused. A delivery within tolerance but above stock -> `INSUFFICIENT_STOCK` (R25).

`sales.reservation`
- AC-70 Set to `"block"`. Receive 10 of A into W1; SO [A, 8] (Available == 2). Issue 3 of A from W1 ->
  refused by (`STOCK_RESERVED`, `sales.reservation`, `"block"`), key `lines[0].quantity`; nothing posted, no
  number consumed. Issue 2 -> posted, Available == 0.
- AC-71 After AC-70: Deliver (1, 8) against the order -> posted; Stock == 0.
- AC-72 A transfer of 3 A from W1 to W2 in the state of AC-70 (before the issue of 2) -> `STOCK_RESERVED`;
  of 2 -> posted. Stock in W2 and orders for W2 play no part.
- AC-73 Reversal: Receive 10 (document D), SO [A, 8], then reverse D -> `STOCK_RESERVED` with the keys of
  D's lines; D stays `posted`. After the order is closed, reversing D -> `201`.
- AC-74 Between orders (E14): SO1 [A, 10] and SO2 [A, 10] with stock 10; Deliver 10 for SO2 -> posted;
  Deliver 10 for SO1 -> `INSUFFICIENT_STOCK` with `rules` naming `stock.negativeStock`.
- AC-75 Already negative (E15): Available −5 from orders beyond stock; Receive 1 -> posted; Issue 1 ->
  `STOCK_RESERVED`; a count of A in W1 with a lower counted quantity -> posted.
- AC-76 With `inform` (after reset) the issue of 3 of AC-70 posts.
- AC-77 Independent of negative stock: `stock.negativeStock` = `allow` and `sales.reservation` = `block`,
  stock 0, SO [A, 5]: Issue 1 -> `STOCK_RESERVED`; after the order is closed, Issue 1 -> posted (stock −1).
- AC-78 Confirming is unaffected: with `block` and stock 0, SO [A, 5] confirms.

`quantity.decimals` and `document.maxLines`
- AC-80 Set(`quantity.decimals`, 2). Creating a receipt, a transfer, a count, a purchase order and a sales
  order with a line `quantity: 1.005` -> each refused by (`VALIDATION_FAILED`, `quantity.decimals`, 2) with
  key `lines[0].quantity`; with `1.5`, `1.50` and `3` -> accepted. The same on replace.
- AC-81 Set to 0: `2` accepted, `0.5` refused. A line `3 box` of an article with factor 0.5 -> accepted,
  `baseQuantity == 1.5` (E8). `unitPrice: 1.123456` on an order line and a conversion factor `0.333333` are
  accepted whatever the rule's value (R33).
- AC-82 Set(`document.maxLines`, 2): a stock document and an order with 3 lines -> refused by
  (`VALIDATION_FAILED`, `document.maxLines`, 2) with key `lines`; with 2 -> accepted.
- AC-83 Set(`document.maxLines`, 1000): a receipt of 1000 lines -> `201`, and it posts; 1001 -> `400`.

Existing data
- AC-90 A draft receipt with a line `quantity: 1.123456` and a draft with 3 lines exist; then
  Set(`quantity.decimals`, 2) and Set(`document.maxLines`, 2). Both drafts read unchanged (`updatedAt`
  included) and **post**; the posted quantity is `1.123456`. Replacing either with its own body -> `400`
  naming the rule. A posted document with such a line can be reversed.
- AC-91 After every Set and reset in AC-50 to AC-83, a document, order, ledger and stock-on-hand read before
  the change is JSON-equal to the same read after it (checked for one Set of each rule).

Timing and concurrency
- AC-95 Read-your-writes: 20 times in a row — Set(`stock.negativeStock`, `"allow"`), post an issue of 1
  from an empty pair (expect posted), Set to `"refuse"`, post an issue of 1 (expect `INSUFFICIENT_STOCK`).
  No iteration differs.
- AC-96 Stock 5; 10 issues of 1 posted in parallel while one request Sets `stock.negativeStock` to
  `"allow"`: every issue is `200` or `INSUFFICIENT_STOCK` whose `rules` value is `"refuse"`; none is `500`;
  Stock == 5 − (number posted); Balanced; verify empty; numbers of the posted issues are gapless.
- AC-97 Two parallel Sets of one rule to different values: both `200`; the rule has one of the two values;
  the history has both changes, and the newest one's `newValue` is the rule's `value`.

Tenant isolation
- AC-100 Tenant X Sets all six rules. In tenant Y: `GET /rules` shows six defaults with `source ==
  "default"`; `GET /rule-changes` -> empty; the five refusals of AC-40 still occur, their `rules` showing
  the defaults.
- AC-101 The same through tools: `rule_list` and `rule_change_list` with Y's key show nothing of X.

MCP
- AC-110 Through tools only, in a new tenant: `rule_list` -> 6 rules, JSON-equal to `GET /rules`;
  `rule_get` `{ key: "sales.reservation" }` -> the rule; `rule_set` `{ key: "stock.negativeStock", value:
  "allow" }` -> the rule with `source == "tenant"`, `updatedBy` the MCP request's key; `stock_document_post`
  of an issue from an empty pair -> posted; `rule_reset` -> default; `rule_change_list` -> two changes.
- AC-111 Tool errors: `rule_set` with value `"yes"` -> `VALIDATION_FAILED`, key `value`; `rule_get`
  `{ key: "nope" }` -> `NOT_FOUND`; `rule_get` `{}` -> `VALIDATION_FAILED`, key `key`; `rule_set` with an
  extra argument -> `VALIDATION_FAILED` with that argument's key.
- AC-112 A tool error carries `rules`: `stock_document_post` of an issue above stock -> error
  `INSUFFICIENT_STOCK` whose JSON has `rules` equal to the HTTP response's `rules` for the same case; with
  `block`, -> `STOCK_RESERVED` likewise.
- AC-113 Parity: for one Set, one reset and one refused posting, the HTTP body and the tool result are
  JSON-equal (timestamps and ids aside).

Unchanged
- AC-120 After the criteria above, in every tenant used: Balanced, and verify is empty.

## 11. Notes for the tester and the builder

Tester
- The weight is on four things: defaults reproduce the built system (AC-40–AC-44); each rule at one other
  value (AC-50–AC-83); a change touches nothing stored and applies at once (AC-90–AC-97); errors name the
  rule (AC-42, the "refused by" form everywhere).
- One tenant per test is not a convenience here but a requirement: a rule set in a shared tenant changes
  other tests' results.
- Do not pin `name` or `description` texts. Do pin keys, types, defaults and allowed values (AC-20): that
  list is an inventory test (architecture §9) and later specs add to it.
- The approved changes to earlier tests are exactly those of AC-01. If an earlier test fails for another
  reason, write it in `docs/questions/012-q.md` before changing it.
- AC-02 to AC-06 are the builder's.

Builder
- Domain: rule definitions (key, group, texts, type, default, allowed) and a registry that lists them; value
  validation; the six checks as functions that take the value. Application: the port `IRules` (typed read by
  definition, for the current tenant) and the five operations. Infrastructure: the two tables, the port's
  implementation — one query for all of a tenant's values per operation, no cache across requests
  (ADR-0020, decision 10).
- Read rules after the per-tenant lock in every operation that takes it; Set and reset take the lock. If an
  operation validates its body before taking the lock, the two body rules (R32, R34) may be read then — R14
  is about the operation seeing one consistent set, and those two are not read again later.
- `AppError` gains the rules that refused; the HTTP mapping and the MCP mapping each write `rules` in one
  place. An error that names no rule serialises without the member.
- R17 in the form "Δ < 0 and Q + Δ < 0" replaces the existing sufficiency comparison everywhere it is made
  (issue, transfer source, reversal); do not keep a second copy for reversals.
- R23 changes three computations (`outstandingBaseQuantity`, the status, and through them incoming and
  reserved): use `max(0, …)` and `>=`; under the default nothing differs.
- R27 needs the pair's reserved quantity before and after inside the posting transaction; the delivery's own
  effect on its order's outstanding quantity is part of "after".
- Implement the three rule types this spec uses. Do not build `boolean`, `text` or `pattern` ahead of the
  rule that needs them.
- Anything unclear or contradictory: `docs/questions/012-q.md`, then continue with the rest.

## 12. Architect's defaults for the owner

Each stands until the owner says otherwise; the rule that carries it is named. The wider decisions
(invariants, order of the batches, what "however they want" covers) are in `docs/rules.md` §5.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | Who may change a rule? | Every tenant key, agents included, until permissions exist; every change is recorded with its key | S1, R10 |
| 2 | When does a change apply? | From the next operation; nothing stored is changed or re-checked | R13, R15 |
| 3 | A draft saved under a wider rule | It still posts; only its next save is judged | R16 |
| 4 | Negative stock allowed, then refused again | Negative balances stay; movements that raise them pass, movements that lower them are refused | R21 |
| 5 | Over-receipt / over-delivery | A percentage of the ordered base quantity per line, 0–1000; no "unlimited" | R22 |
| 6 | An order received above 100 % | Outstanding is 0, status `full`; it still has to be closed by hand | R23 |
| 7 | What `block` protects | Reserved stock against manual issues, transfers and reversals — not one order against another; no allocation | R27, R28 |
| 8 | Is a stock count blocked by reservation or by the negative-stock rule? | No — it states what is there | R29 |
| 9 | Does `quantity.decimals` limit conversion results, prices or factors? | No — only the quantity the caller enters | R33 |
| 10 | Most lines a tenant can allow | 1000 | 6.1 |
| 11 | Setting a rule to the value of its default | Counts as the tenant's own value and is kept if we later change the default | R7 |
