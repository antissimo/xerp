# Spec 012 — Configurable rules: the mechanism and the stock and order policies

Status: ready for the tester and the builder. **Rewritten 2026-10-10 after the owner's decisions** (ADR-0020,
"Owner's decisions"): a rule is a boolean switch. What changed against the first text: section 13.
Owner requirement of 2026-10-10: every validation and business rule is configurable per tenant; we ship the
default (`CLAUDE.md`, ADR-0020, architecture §11).
Branches: `tests/012-configurable-rules` (tester), `feat/012-configurable-rules` (builder).
Read first: **ADR-0020** (the mechanism — this spec is its contract), `docs/rules.md` (which rules and why),
specs **005** and 006 (posting, negative stock, reversal), **009** and **010** (orders, their partner, order
quantities, reservation), **011a** (the partner of a linked document), 011 (stored balance, the per-tenant
lock), 008 (count).
Why this spec and why this number: `docs/roadmap.md` section 3, row 12.

**Inherited, not re-specified** (roadmap §4): everything specs 001–011a established — transport, errors,
references, MCP mapping, documents, posting, reversal, orders, balances. This spec states only what is new or
changed. Numbers are local; "009/R26" means rule R26 of spec 009.

**The one sentence that governs everything below: a tenant that sets no rule behaves exactly as specs
001–011a say.** Every default is today's behaviour.

**Decisions of the architect that the owner may change** are marked *(default)* and collected in section 12.

## 1. Goal

A tenant — a person or an agent — can see every configurable rule of the system as a yes/no switch with a
plain-language name, its default and its current value, switch it, put it back, and see who changed what.
Six rules that until now were fixed are the first on it:

| Key | The switch | Default |
|---|---|---|
| `stock.negativeStockAllowed` | Stock may go below zero | no |
| `purchase.overReceiptAllowed` | More than ordered may be received on a purchase order | no |
| `sales.overDeliveryAllowed` | More than ordered may be delivered on a sales order | no |
| `sales.reservedStockProtected` | Reserved stock is protected | no |
| `purchase.partnerRequired` | Partner (supplier) required on a purchase order | yes |
| `sales.partnerRequired` | Partner (customer) required on a sales order | yes |

When one of them refuses an operation, the error says which.

## 2. Scope

In scope
1. The rule registry; a boolean value per tenant and rule; history of changes.
2. Five operations over HTTP and five MCP tools: list, get, set, reset, list changes.
3. The six rules of section 6.1, read by the existing checks instead of their constants.
4. The `rules` member of the error document and of the MCP tool error; one new error code,
   `STOCK_RESERVED`.
5. Removal of the check constraint `StockBalance.Quantity >= 0`; the partner of an order becomes nullable.

Out of scope
- Every other rule of `docs/rules.md` §3 (batches 013, 014, number series, permissions). They stay fixed
  checks with their default's behaviour until their spec — among them "partner required on a receipt / an
  issue that is not linked to an order" and the role of the partner (014).
- **Any value that is not `true` or `false`** (owner's decision 2): no percentages, numbers, texts, choices.
  The number of decimals of a quantity (6) and the number of lines of a document (200) are platform bounds
  (`docs/rules.md` I10) and stay fixed checks; they are not rules.
- Who may change rules: every tenant key (owner's decision 3), until permissions (roadmap 021).
- Values per warehouse, article, partner or key; tenant-written rules; effective dates (ADR-0020, "What it
  cannot do").
- Allocation of stock to a particular order; refusing the confirmation of an order (`docs/rules.md`, 014).
- Cost of negative stock (valuation, roadmap 015).

## 3. Data

`RuleValue` (tenant-owned): `TenantId`, `Key` (text, max 100), `Value` (boolean), `UpdatedAt`, `UpdatedBy`.
Primary key `(TenantId, Key)`. A row exists only for a rule the tenant has set; reset removes it.

`RuleChange` (tenant-owned, append-only): `Id` uuid v7, `TenantId`, `Key`, `Action` (`set` | `reset`),
`OldValue`, `NewValue` (booleans, the effective values before and after), `ChangedAt`, `ChangedBy`.

`UpdatedBy` and `ChangedBy` are foreign keys `(TenantId, …)` -> `ApiKey (TenantId, Id)`, `ON DELETE RESTRICT`
(architecture §8). No foreign key on `Key`: the registry is code.

`StockBalance` loses its check constraint `Quantity >= 0` (011 §3).

`PurchaseOrder.SupplierId` and `SalesOrder.CustomerId` become nullable (009 §3, 010 §3); their foreign keys
and indexes stay.

One migration. It adds the two tables, empty, drops the constraint and makes the two columns nullable. No
existing row changes.

## 4. Operations — HTTP

All under `/api/v1`, tenant key required. Bodies, query strings and errors follow specs 001–003.

| Operation | Request | Success |
|---|---|---|
| List rules | `GET /rules?search=&group=&source=&limit=&offset=` | `200` list envelope of rules |
| Get rule | `GET /rules/{key}` | `200` rule |
| Set rule | `PUT /rules/{key}` with body `{ "value": true \| false }` | `200` rule |
| Reset rule | `POST /rules/{key}/reset`, no body | `200` rule |
| List changes | `GET /rule-changes?key=&limit=&offset=` | `200` list envelope of changes |

Rule (exactly these nine properties, always present):

```json
{ "key": "purchase.partnerRequired", "group": "purchase",
  "name": "Partner (supplier) required on a purchase order",
  "description": "When true, a purchase order cannot be saved without a supplier. When false, …",
  "default": true, "value": true, "source": "default",
  "updatedAt": null, "updatedBy": null }
```

- `default` and `value` are JSON booleans. `value` is the value in force for the tenant; `source` is
  `"tenant"` when the tenant has set it and `"default"` otherwise (then `value == default`).
- `true` means: the statement in `name` holds.
- `updatedAt` / `updatedBy` (API key id) are those of the tenant's last set or reset of this rule; `null`
  while the tenant has never changed it.
- `name` and `description` are English text for a reader; they are not contract and tests do not pin them.
- There is no `type` and no `allowed`.

Change: `{ "id", "key", "action": "set" | "reset", "oldValue", "newValue", "changedAt", "changedBy" }`;
`oldValue` and `newValue` are JSON booleans.

Errors:

| HTTP | `code` | When | `errors` keys |
|---|---|---|---|
| 400 | `VALIDATION_FAILED` | Body malformed, unknown property, `value` missing; `value` not a JSON boolean; a bad query parameter | `body`, the property, `value`, the parameter |
| 404 | `NOT_FOUND` | `{key}` is not a rule of the registry (any string) | — |

New error code (registry: architecture §6):

| HTTP | `code` | Meaning | `errors` keys |
|---|---|---|---|
| 409 | `STOCK_RESERVED` | With `sales.reservedStockProtected` = `true`: the posting or reversal would take goods that confirmed sales orders reserve (R27). | `lines[i].quantity` |

**The `rules` member** (architecture §6, ADR-0020 decision 9). A refusal caused by one of the six rules adds
to the problem document:

```json
"rules": [ { "key": "stock.negativeStockAllowed", "value": false, "fields": ["lines[0].quantity"] } ]
```

one element per rule that refused, `value` the boolean it had, `fields` the `errors` keys it produced (in the
order they appear in `errors`). The member is absent when none of the six refused.

**Changes to existing operations** (orders, 009 §4 and 010 §4; details in 6.7):

| Operation | Change |
|---|---|
| Create purchase order / sales order | `supplierId` / `customerId` may be omitted or `null` (the two are equal). Whether an order without a partner is accepted is decided by the rule. |
| Replace | `supplierId` / `customerId` must still be present; it may be `null`. |
| Every representation and list summary of an order | `supplier` / `customer` is always present and is `null` for an order without a partner. |
| Stock document linked to such an order | `partner` is `null` (011a/R8). |

## 5. Operations — MCP

Five new tools; `tools/list` returns exactly 62: the 57 of spec 011 and `rule_list`, `rule_get`, `rule_set`,
`rule_reset`, `rule_change_list`.

| Tool | Arguments | Result |
|---|---|---|
| `rule_list` | `search?`, `group?`, `source?`, `limit?`, `offset?` | as `GET /rules` |
| `rule_get` | `key` | the rule |
| `rule_set` | `key`, `value` (boolean) | the rule |
| `rule_reset` | `key` | the rule |
| `rule_change_list` | `key?`, `limit?`, `offset?` | as `GET /rule-changes` |

- Mapping, attribution and errors as 003/R12–R17. `key` is the addressing argument: missing, `null` or not a
  string -> `VALIDATION_FAILED` with key `key`; a string that is no rule -> `NOT_FOUND`. `value` missing or
  not a boolean -> `VALIDATION_FAILED` with key `value`.
- A tool error carries `rules` exactly as the HTTP problem does: `{ "code", "detail", "errors"?, "rules"? }`.
- Annotations: `rule_list`, `rule_get`, `rule_change_list` read-only; `rule_set`, `rule_reset` not read-only,
  idempotent, not destructive.
- **Changed schemas of existing tools** — the same for every tenant, whatever the rules' values
  (architecture §11: a rule never changes the shape of the contract):
  - `purchase_order_create`: `supplierId?` (uuid or null) — no longer in `required`;
    `sales_order_create`: `customerId?` likewise;
  - `purchase_order_update` / `sales_order_update`: `supplierId` / `customerId` stays required and may be
    `null`;
  - the output schema of every tool that returns an order or its summary: `supplier` / `customer` nullable.
    (`partner` of a stock document is nullable already, 011a.)
- Descriptions say:
  - `rule_list` — these are the business rules this company can switch on or off; each is a statement that
    is true or false and has a default; read `description` before changing one.
  - `rule_set` — changes the rule for the whole company and for every user and agent, from the next
    operation on; existing data is not changed; the change is recorded with your key. Do not change a rule
    only to get one refused operation through unless the user asked for the rule to change.
  - `rule_reset` — returns the rule to its default.
  - `stock_document_post`, `stock_document_reverse` — name the rules that can refuse
    (`stock.negativeStockAllowed`, `sales.reservedStockProtected`, and for a linked document
    `purchase.overReceiptAllowed` / `sales.overDeliveryAllowed`) and add `STOCK_RESERVED` to their error
    codes; say that a refusal's `rules` names the rule.
  - `purchase_order_create` / `_update`, `sales_order_create` / `_update` — say that the supplier / customer
    is required unless `purchase.partnerRequired` / `sales.partnerRequired` is false.

## 6. Rules

### 6.1 Rules this spec introduces (architecture §11)

| Key | Name | Default | Replaces the fixed check |
|---|---|---|---|
| `stock.negativeStockAllowed` | Stock may go below zero | `false` | 005/R15–R16, 006/R7, R16, 008/R18, 010/R9 |
| `purchase.overReceiptAllowed` | More than ordered may be received on a purchase order | `false` | 009/R26 |
| `sales.overDeliveryAllowed` | More than ordered may be delivered on a sales order | `false` | 010/R7 (009/R26 mirrored) |
| `sales.reservedStockProtected` | Reserved stock is protected | `false` | 010/R17 |
| `purchase.partnerRequired` | Partner (supplier) required on a purchase order | `true` | 009/R1 ("both are required", for the supplier) |
| `sales.partnerRequired` | Partner (customer) required on a sales order | `true` | 010/R1 (009/R1 mirrored) |

Checks this spec adds that are **invariants** (`docs/rules.md` §2, I12 — the mechanism itself): a value is a
JSON boolean (R6); a key must be a rule of the registry (R4); a change is attributed and kept (R10–R11).

Checks of earlier specs that this spec **leaves fixed, as invariants** (I10, platform bounds — the first text
of this spec made them rules): a quantity has at most 6 decimal places (005/R6); a document or an order has
at most 200 lines (005/R4, 008/R2, 009/R5). Their refusals carry no `rules` member.

### 6.2 The registry and its operations

- R1. The registry of this spec has exactly the six rules of 6.1. `GET /rules` lists all of them for every
  tenant, set or not; `total` is 6 without filters.
- R2. List: ordered by `key`, ascending, ordinal. `group` filters by exact value (a group that no rule has ->
  empty list); `source` accepts exactly `default` or `tenant`; `search` as 001/R9, matching `key` or `name`.
  Filters combine with AND; `limit` / `offset` as everywhere.
- R3. A tenant that has set nothing: every rule has `value == default`, `source == "default"`,
  `updatedAt == null`, `updatedBy == null`. Creating a tenant writes no rule value.
- R4. `{key}` addresses the rule and is compared exactly (case-sensitive). Anything that is not one of the
  registry's keys — another case, an empty or over-long string, a key of a later batch, a key of the first
  text of this spec (`stock.negativeStock`, `quantity.decimals`, …) — is `404 NOT_FOUND`.
- R5. **Set.** Order of checks: form of the body (`400`: malformed, unknown property, `value` missing) ->
  the rule exists (`404`) -> the value (`400`, key `value`). On success the tenant has that value:
  `source == "tenant"`, `updatedAt` = now, `updatedBy` = the acting key.
- R6. **A value is valid** when it is the JSON literal `true` or `false`. A string (`"true"`, `"yes"`), a
  number (`1`, `0`), `null`, an array or an object -> `400` with key `value`; nothing changes.
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
- R15. **Nothing stored is touched by a change.** No document, line, ledger entry, balance, order, order
  quantity or number changes, is re-checked or is refused later *for having been written* under another
  value. Reading never applies a rule.
- R16. Which operation applies which rule:
  - `purchase.partnerRequired` and `sales.partnerRequired` judge the request body of **create and replace**
    of a purchase order / a sales order — nothing else. Confirmation, closing, reopening and fulfilment do
    not look at them: a draft saved without a partner while that was allowed confirms as it is.
  - `stock.negativeStockAllowed` and `sales.reservedStockProtected` judge **posting and reversal** of stock
    documents.
  - `purchase.overReceiptAllowed` and `sales.overDeliveryAllowed` judge **posting** of a linked receipt /
    delivery.

### 6.4 `stock.negativeStockAllowed`

- R17. For a posting or a reversal, and for every (article, warehouse) pair it writes entries for, let Δ be
  the sum of those entries and Q the pair's stock on hand before. With `false`, the operation is refused
  with `409 INSUFFICIENT_STOCK` when for any pair **Δ < 0 and Q + Δ < 0**. Keys and everything else as
  005/R15, 006/R7 and R16, 008/R18, 010/R9. While no pair is negative this is exactly the rule as built.
- R18. With `true` there is no such check: `INSUFFICIENT_STOCK` is never answered. Stock on hand, the
  stored balance and `availableQuantity` may be negative. Everything else about posting is unchanged —
  references, conversion, order checks, numbering, atomicity.
- R19. Invariants that hold for either value (`docs/rules.md` I5): stock on hand == sum of the ledger ==
  stored balance; verify (`GET /stock-balance-differences`) is empty and rebuild corrects nothing, also with
  negative pairs.
- R20. Reading with negative stock: stock on hand lists a pair whose `quantity` is not zero, negative
  included (005/R20). In the stock list of a warehouse `hasStock=true` keeps `quantity > 0` and
  `hasStock=false` keeps `quantity <= 0`. A count's book quantity may be negative; its difference is counted
  − book (008/R7), and posting it brings the pair to the counted quantity (008/R12).
- R21. After `true` is changed back to `false`, negative pairs stay as they are (R15). By R17 a posting
  that raises such a pair is accepted even if it stays negative; one that lowers it is refused.

### 6.5 `purchase.overReceiptAllowed` and `sales.overDeliveryAllowed`

Stated for purchasing; it holds mirrored for deliveries against sales orders with `sales.overDeliveryAllowed`.

- R22. With `false`, posting a linked receipt is refused with `409 QUANTITY_EXCEEDS_ORDER` when, for any
  order line, `receivedBaseQuantity` before the posting plus the document's `baseQuantity` for that line
  exceeds the line's `baseQuantity` — 009/R26 as built, with its keys, atomicity and place in the order of
  checks. With `true` there is no such check: `QUANTITY_EXCEEDS_ORDER` is never answered for a receipt, and
  **there is no upper limit** (a rule is yes or no; there is no tolerance percentage).
- R23. `receivedBaseQuantity` is still the sum over posted, non-reversing documents (009/R25) and may now
  exceed `baseQuantity`. `outstandingBaseQuantity` is `max(0, baseQuantity − receivedBaseQuantity)` while
  the order is `confirmed` (otherwise 0): never negative. `receiptStatus` is `"full"` when every line has
  `receivedBaseQuantity >= baseQuantity`. `incomingQuantity` (009/R35) and `reservedQuantity` (010/R15) sum
  the outstanding quantities and are therefore never lowered by an excess.
- R24. The value at posting decides. Switching back to `false` changes no received quantity (R15); a further
  receipt for a line that is already at or above its ordered quantity is refused. A reversal is not judged
  by this rule (009/R34).
- R25. A delivery is still judged for stock (R17) and reservation (R27) after the order's quantities
  (010/R7): the rule allows more than ordered, not more than there is.

### 6.6 `sales.reservedStockProtected`

- R26. With `false`, reserved quantity blocks nothing (010/R17, as built).
- R27. With `true`: for a posting or a reversal, and for every pair with Δ < 0 (R17), let
  `A_before` = `availableQuantity` of the pair before the operation and `A_after` the value it would have
  after it — quantity and reserved quantity both as they would then be. The operation is refused with
  `409 STOCK_RESERVED` when for any pair **the reserved quantity after it is above 0, `A_after < 0` and
  `A_after < A_before`**: something is still reserved, stock no longer covers it, and this operation made
  that worse. `errors` keys are `lines[i].quantity` for every line of the document with the article of such
  a pair (for a reversal: the original's lines, as 006/R16).
- R28. What follows from R27:
  - an unlinked issue and a transfer out of a warehouse cannot take stock below what confirmed sales orders
    for that warehouse still await;
  - a delivery against a sales order, up to the order line's outstanding quantity, lowers `quantity` and
    `reservedQuantity` alike, leaves `availableQuantity` unchanged, and is therefore **never** refused by
    this rule — whichever order it is for. Between orders the first delivery posted gets the goods; stock
    is not allocated to an order (out of scope);
  - the part of a delivery above the outstanding quantity (possible with `sales.overDeliveryAllowed`) lowers
    availability and is judged;
  - reversing a receipt, a transfer (at its destination) or a count that added stock lowers a pair and is
    judged; reversing an issue or a delivery never is.
- R29. **Posting a count is never judged by this rule** (nor by R17; 008/R13): a count states what is
  physically there. *(default)*
- R30. Order of checks on posting, extending 010/R7: … -> the order is `confirmed` -> the order's quantities
  (`QUANTITY_EXCEEDS_ORDER`) -> stock (`INSUFFICIENT_STOCK`) -> reservation (`STOCK_RESERVED`). On reversal:
  … -> stock -> reservation. Each stage answers alone. The two rules are independent: with
  `stock.negativeStockAllowed` = `true` and `sales.reservedStockProtected` = `true`, R27 still applies.
- R31. Confirming, closing and reopening a sales order are not affected by this rule (010/R4): availability
  can still become negative by confirming orders; R27 then refuses whatever would lower it further.

### 6.7 `purchase.partnerRequired` and `sales.partnerRequired`

Stated for purchase orders (`supplierId`, `supplier`, `purchase.partnerRequired`); it holds mirrored for
sales orders (`customerId`, `customer`, `sales.partnerRequired`). The two rules are separate.

- R32. **The shape, the same for every tenant** (an invariant, I9): `supplierId` is a uuid or `null`. On
  create, omitted and `null` are equal and mean "no supplier". On replace the property must be present
  (replace carries every field); it may be `null`. A value that is neither a uuid nor `null`, and a replace
  body without the property, are `400` with key `supplierId` and **no** `rules` member — whatever the
  rule's value.
- R33. **With `true`** an order without a supplier is refused on create and on replace:
  `400 VALIDATION_FAILED` with key `supplierId`, naming the rule (R38). It is part of validation: reported
  together with every other invalid field of the body (009/R10, first stage). With a supplier given, the
  order is judged exactly as in spec 009.
- R34. **With `false`** an order without a supplier is accepted. Its `supplier` is `null`. An order *with* a
  supplier is judged exactly as before: the supplier must exist, be active when newly assigned and have the
  role (`REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`, `PARTNER_ROLE_MISSING`, 009/R2, R10) — the rule makes
  the partner optional, not unchecked. A replace may remove the supplier of a draft (`null`) and may give
  one to a draft that had none.
- R35. An order without a supplier is an order like any other: it is confirmed (the supplier checks of
  009/R12 apply only when there is a supplier), numbered, closed and reopened; its outstanding quantities
  count as `incomingQuantity` (sales: `reservedQuantity`); it is received against (R36). The list filter
  `supplierId=` returns only orders with that supplier, so never this one; there is no filter for "without a
  supplier" *(default)*.
- R36. **A stock document linked to an order without a partner has no partner**: 011a/R8 reads "the
  partner of a linked document is the order's partner — `null` when the order has none". `partnerId`
  omitted or `null` gives `partner == null`; any non-null `partnerId` is `409 ORDER_MISMATCH` with key
  `partnerId` (011a/R9, unchanged). Its reversing document has `partner == null` (011a/R14).
- R37. **The rule judges saving, not the life of the order** (R16, ADR-0020 decision 7). After the rule is
  `true` again, an order without a supplier that already exists is read, confirmed, received against,
  closed and reopened as before; only a replace of it (a draft) must name a supplier. *(default)*

### 6.8 Errors name the rule

- R38. Every refusal by R17, R22, R27 or R33 carries `rules` with that rule's key, the value in force
  and, as `fields`, exactly the `errors` keys that rule produced:

  | Refusal | `rules[].key` | `value` |
  |---|---|---|
  | `INSUFFICIENT_STOCK` | `stock.negativeStockAllowed` | `false` |
  | `QUANTITY_EXCEEDS_ORDER` on a receipt / on a delivery | `purchase.overReceiptAllowed` / `sales.overDeliveryAllowed` | `false` |
  | `STOCK_RESERVED` | `sales.reservedStockProtected` | `true` |
  | `VALIDATION_FAILED`, a purchase order / a sales order without a partner | `purchase.partnerRequired` / `sales.partnerRequired` | `true` |

- R39. A `VALIDATION_FAILED` reports all invalid fields together as before. `rules` has one element per rule
  that contributed; fields that an invariant refused (a negative quantity, an unknown property, a quantity
  with seven decimals, a 201st line) are in `errors` and in no element's `fields`. A response in which no
  rule refused has no `rules` member — not an empty array.
- R40. Error codes, statuses and `errors` keys of specs 001–011a are unchanged.

## 7. Edge cases

- E1. `PUT /rules/stock.negativeStockAllowed` with `{}` -> `400`, key `value`; with
  `{ "value": true, "x": 1 }` -> `400`, key `x`; with `{ "value": v }` for v = `"true"`, `"yes"`, `"allow"`,
  `1`, `0`, `null`, `[]`, `{}` -> `400`, key `value`. `true` and `false` -> `200`.
- E2. `PUT /rules/nope`, `PUT /rules/Stock.NegativeStockAllowed`, `PUT /rules/stock.negativeStock`,
  `PUT /rules/partner.roleRequired` (a later batch) with a valid body -> `404`; with `{}` -> `400` (form
  first, R5).
- E3. `DELETE /rules/{key}`, `POST /rules` -> `404` (001/E11). `GET /rules?foo=1` -> `400`, key `foo`;
  `?source=mine` -> `400`, key `source`; `?group=nope` -> `200`, empty.
- E4. Set, reset, then set the same value again -> three changes in the history. Setting the same value
  twice in a row -> one (R8).
- E5. Over-receipt allowed, order line of 3 boxes of 12 (base 36): receipts of 36 and then 100 post;
  received 136, outstanding 0, status `full`. The rule is switched off again: the line still shows 136; any
  further receipt for it is refused; reversing the receipt of 100 works and leaves 36.
- E6. Reserved stock protected: stock 10, a confirmed sales order for 8 (available 2). Unlinked issue of 2
  -> posted (available 0); of 3 -> `STOCK_RESERVED`. Delivery of 8 against the order -> posted.
- E7. Protected: stock 10, orders SO1 for 10 and SO2 for 10 confirmed (available −10). Delivery of 10 for
  SO2 -> posted; delivery for SO1 -> `INSUFFICIENT_STOCK` (not `STOCK_RESERVED`: stock comes first, R30).
- E8. Protected: available is −5 because orders were confirmed beyond stock; a receipt posts; an unlinked
  issue of any quantity is refused (it lowers availability further); a count that finds less posts (R29).
- E9. Negative stock allowed: an issue of 5 from an empty pair -> posted; stock on hand lists the pair with
  `-5`; verify is empty. A transfer of 5 out of an empty warehouse -> posted: −5 there, +5 at the
  destination.
- E10. Negative stock refused again with a pair at −5: receipt of 3 -> posted (−2); issue of 1 -> refused;
  reversal of the receipt of 3 -> refused; a count of 0 -> posts, the pair is 0.
- E11. A rule set by a key that is revoked afterwards keeps its value; `updatedBy` still names that key.
- E12. Partner required (default) and a body with an invalid `orderDate`, no `supplierId` and a line
  `quantity: -1` -> `400` with the three keys; `rules` has one element, `purchase.partnerRequired`, with
  `fields` `["supplierId"]` (R39).
- E13. Partner not required: `supplierId` a random uuid -> `REFERENCE_NOT_FOUND`; a partner without the
  supplier role -> `PARTNER_ROLE_MISSING` (R34); `"abc"` -> `400` without `rules` (R32).
- E14. Partner not required, a draft order without a supplier; the rule is set to `true`; the draft confirms
  and gets its number (R37); a new order without a supplier is refused.

## 8. Tenant isolation

- T1. `RuleValue` and `RuleChange` are tenant-owned: `TenantId`, query filter, tenant-inclusive keys. The
  registry (definitions and defaults) is code and the same for all tenants; it contains no tenant data.
- T2. A value set by tenant X changes nothing for tenant Y: Y's `GET /rules` shows its own values, Y's
  operations are judged by Y's values, Y's `GET /rule-changes` shows only Y's changes.
- T3. `rules` in an error names the caller's tenant's value only.
- T4. The same through the tools. No `IgnoreQueryFilters()` is added.

## 9. Security requirements

- S1. All five operations require a tenant key (`401` / `403` as 001/S1–S4). Every tenant key, human or
  agent, may read and change rules (owner's decision 3; restrictable from roadmap 021).
- S2. Every change is attributable: the rule shows the last key and time, the history every change (R10).
  No operation edits or deletes history.
- S3. Rule values are not secrets, but they are the tenant's: they appear only in that tenant's responses.
  Logs may record rule key, value, API key id and tenant id.
- S4. No value can be stored that the checks cannot handle: a value is a boolean, and validation (R6) is the
  only way in. A stored row whose key is no rule of the registry is ignored — it never causes `500`.
- S5. No input in `{key}` or `value` may cause `500` (001/R16).

## 10. Acceptance criteria

Conventions and setup as in specs 009 §10, 010 §10 and 011 §10: the standard setup (unit `pcs`, stock
articles `A` and `B`, warehouses `W1` and `W2`), unit `box` with A / box = 12, partners `SUP` and `CUS`,
"PO […]", "SO […]", "Receive n of A into W1", "Receive (k, n) against the order", "Deliver (k, n) against
the order", "Stock(A, W)", "Available(A, W)", "Balanced". In addition:
- "Set(key, v)" means `PUT /rules/{key}` with `{ "value": v }` answered `200`; v is `true` or `false`.
- "Issue n of A from W1" means: create an unlinked issue with one line and post it.
- "refused by (CODE, key, v)" means: the response has that `code`, a non-empty `errors`, and `rules` with
  exactly one element whose `key` and `value` are those and whose `fields` equal the keys of `errors`.
Each test uses its own tenant, so rules never leak between tests. Unmarked criteria are black-box (tester).
Numbers that the first text of this spec used and that no longer exist are left unused (AC-45 onwards are
not renumbered).

Structure
- AC-01 *(manual)* Build and tests exit 0; earlier tests pass unweakened. **No test of specs 001–011a that
  asserts a status, a code, an `errors` key, a quantity, a number or a state changes** — they run in tenants
  that set no rule (AC-40). The **only** earlier tests changed:
  1. the literal tool list and its count (57 -> 62);
  2. tests asserting the **exact member set** of a problem document or tool error for `INSUFFICIENT_STOCK`,
     `QUANTITY_EXCEEDS_ORDER`, or `VALIDATION_FAILED` caused by a missing or `null` `supplierId` /
     `customerId` on **create** of an order (they gain `rules`);
  3. tests pinning the error-code list in the description of `stock_document_post` or
     `stock_document_reverse` (they gain `STOCK_RESERVED`);
  4. tests pinning the input schema of `purchase_order_create` / `sales_order_create` (`supplierId` /
     `customerId` leaves `required` and becomes nullable), of `purchase_order_update` / `sales_order_update`
     (nullable), or the output schema of an order (`supplier` / `customer` nullable);
  5. the builder's test that the database refuses a negative `StockBalance.Quantity` (011): removed; AC-52
     takes its place. A builder's model test that `SupplierId` / `CustomerId` is not nullable, if one
     exists: changed to nullable.
  One migration added.
- AC-02 *(builder, model)* The model and table tests pass with `RuleValue` and `RuleChange`; both are
  tenant-owned and filtered.
- AC-03 *(builder, unit)* R17 and R27 as pure functions of Δ, Q, reserved and the rule's boolean; R22 as a
  function of received, document and ordered quantity and the boolean.
- AC-04 *(manual)* The six fixed decisions are gone from the checks: each of the six behaviours is decided
  by a boolean obtained through `IRules` and passed to a Domain function; `Api` reads no rule
  (architecture §11).
- AC-05 *(builder)* A `RuleValue` row whose key is no rule of the registry (written directly) is ignored: it
  is not listed, and nothing fails (S4).
- AC-06 *(builder)* Migration: on a database at the previous migration with posted documents and orders,
  afterwards both tables exist and are empty, `StockBalance` has no check constraint on `Quantity`,
  `SupplierId` and `CustomerId` are nullable with every existing value kept, and Balanced holds.

Inherited behaviour — smoke
- AC-10 Each of the five routes without a credential -> `401`; with the admin key -> `403`.
- AC-11 `GET /rules?foo=1` -> `400`, key `foo`; `PUT /rules/stock.negativeStockAllowed` with an unknown
  property -> `400` with that property's key; `GET /rules?limit=0` -> `400`, key `limit`.
- AC-12 `tools/list` returns exactly 62 names (literal list).

The registry
- AC-20 In a new tenant `GET /rules` -> `200`, `total == 6`, keys in this order with these defaults
  (literal — the inventory of rules): `purchase.overReceiptAllowed` `false`, `purchase.partnerRequired`
  `true`, `sales.overDeliveryAllowed` `false`, `sales.partnerRequired` `true`,
  `sales.reservedStockProtected` `false`, `stock.negativeStockAllowed` `false`. For each: `value == default`
  (a JSON boolean), `source == "default"`, `updatedAt == null`, `updatedBy == null`, `group` equal to the
  key's first segment, non-empty `name` and `description`, and exactly the nine properties of section 4
  (no `type`, no `allowed`).
- AC-21 `GET /rules/sales.reservedStockProtected` -> the same object as in the list. `GET /rules/nope`,
  `/rules/Sales.ReservedStockProtected`, `/rules/sales.reservation` -> `404`.
- AC-22 Filters: `group=sales` -> the three `sales.` rules; `source=tenant` -> empty in a new tenant, and
  after one Set exactly that rule; `search=partnerRequired` -> the two partner rules; `group=nope` -> empty.

Set, reset, history
- AC-30 Set(`stock.negativeStockAllowed`, `true`) -> `200` with `value == true`, `source == "tenant"`,
  `default == false`, `updatedBy` = the acting key's id, `updatedAt` set; `GET` shows the same.
- AC-31 Invalid values (E1: `"true"`, `"yes"`, `1`, `0`, `null`, `[]`, `{}`, and a body without `value`) ->
  `400` with key `value`; the rule is unchanged and no change is recorded.
- AC-32 Order of checks (E2): unknown key with `{}` -> `400`; unknown key with a valid body -> `404`.
- AC-33 Setting the default's own value: Set(`purchase.partnerRequired`, `true`) in a new tenant ->
  `source == "tenant"`, `value == true`; one change with `oldValue == true`, `newValue == true`.
- AC-34 Setting the same value twice: the second answers `200` with the same `updatedAt`; one change.
- AC-35 `POST /rules/stock.negativeStockAllowed/reset` after AC-30 -> `value == false`,
  `source == "default"`, `updatedAt` later than before, `updatedBy` the acting key. A second reset -> `200`,
  nothing changes. Reset of an unknown key -> `404`.
- AC-36 History: after Set to `true` by key K1 and reset by key K2 (an `agent` key),
  `GET /rule-changes?key=stock.negativeStockAllowed` -> two items, newest first: `{ action: "reset",
  oldValue: true, newValue: false, changedBy: K2 }`, `{ action: "set", oldValue: false, newValue: true,
  changedBy: K1 }`; `changedAt` of each equals the rule's `updatedAt` after that change. Without `key` the
  list has the changes of all rules; `key=nope` -> empty.
- AC-37 Every rule accepts both values: for each of the six, Set to `true` and Set to `false` -> `200`, and
  `GET` shows the value.

Defaults reproduce specs 001–011a
- AC-40 In a tenant that sets nothing: an issue above stock -> `INSUFFICIENT_STOCK`; a receipt above its
  order -> `QUANTITY_EXCEEDS_ORDER`; a delivery above its order -> `QUANTITY_EXCEEDS_ORDER`; an unlinked
  issue of goods a confirmed order reserves -> posted; a purchase order without `supplierId` and a sales
  order without `customerId` -> `400` with that key.
- AC-41 The same in a tenant in which each of the six rules was Set to the other value and then reset.
- AC-42 **Errors name the rule**: the five refusals of AC-40 are "refused by" (`INSUFFICIENT_STOCK`,
  `stock.negativeStockAllowed`, `false`), (`QUANTITY_EXCEEDS_ORDER`, `purchase.overReceiptAllowed`,
  `false`), (`QUANTITY_EXCEEDS_ORDER`, `sales.overDeliveryAllowed`, `false`), (`VALIDATION_FAILED`,
  `purchase.partnerRequired`, `true`), (`VALIDATION_FAILED`, `sales.partnerRequired`, `true`).
- AC-43 A refusal no rule caused has no `rules` member: `VALIDATION_FAILED` for a negative quantity, for an
  empty `lines`, for an unknown property, for a quantity `1.0000001` (seven decimals), for 201 lines, for
  `supplierId: "abc"` and for a replace body without `supplierId`; `REFERENCE_NOT_FOUND`; `INVALID_STATE`;
  `ORDER_NOT_OPEN`; `COUNT_OUTDATED`; `PARTNER_ROLE_MISSING`. A quantity `1.000001` and 200 lines are
  accepted, in every tenant.
- AC-44 Mixed (E12): the response has the three `errors` keys, and `rules` has exactly one element,
  `purchase.partnerRequired` with `value == true`, whose `fields` is `["supplierId"]`.

`stock.negativeStockAllowed`
- AC-50 Set to `true`. Issue 5 of A from W1 (empty) -> posted; Stock(A, W1) == −5; the pair is listed by
  `GET /stock-on-hand` with `quantity == -5` and `availableQuantity == -5`; Balanced;
  `GET /stock-balance-differences` -> `total == 0`.
- AC-51 `true`: a transfer of 5 A from W1 (empty) to W2 -> posted; Stock(A, W1) == −5, Stock(A, W2) == 5.
  Reversing a receipt whose goods were issued since -> `201`. A delivery above stock (within its order) ->
  posted.
- AC-52 `true`, with negative pairs: `POST /stock-balances/rebuild` -> `corrected == 0`; verify empty; the
  stock list of W1 with `hasStock=false` contains A, with `hasStock=true` does not.
- AC-53 `true`: a count of A in W1 at −5 shows `bookQuantity == -5`; counted 2 -> `differenceQuantity == 7`;
  posted; Stock(A, W1) == 2.
- AC-54 Back to `false` (E10), pair at −5: Receive 3 -> posted, −2; Issue 1 -> refused by
  (`INSUFFICIENT_STOCK`, `stock.negativeStockAllowed`, `false`); reversing the receipt of 3 ->
  `INSUFFICIENT_STOCK`; nothing stored changed when the rule changed (the documents, the ledger and the −5
  read the same before and after the Set); Balanced throughout.

Over-receipt and over-delivery
- AC-60 Set(`purchase.overReceiptAllowed`, `true`). PO [A, 3 box, price 1] (base 36). Receive (1, 36) ->
  posted; Receive (1, 100) -> posted; Received(1) == 136, Outstanding(1) == 0, `receiptStatus == "full"`,
  Incoming(A, W1) == 0.
- AC-61 `true`: one receipt of 500 against a fresh order as in AC-60 -> posted (no limit). In a tenant at
  the default: one receipt of 37 -> refused (nothing posted, no number consumed); of 36 -> posted.
- AC-62 Switching back (E5): after AC-60, Set to `false` -> the order still shows Received(1) == 136 and
  `full`; a further receipt of 1 is refused by (`QUANTITY_EXCEEDS_ORDER`, `purchase.overReceiptAllowed`,
  `false`); reversing the receipt of 100 -> `201`, Received(1) == 36; a receipt of 1 is still refused (36
  of 36).
- AC-63 Partial then excess: `true`, order line of base 100; Receive 50 -> Outstanding 50, `partial`;
  Receive 60 -> posted, Received 110, Outstanding 0, `full`.
- AC-64 Mirrored for sales with `sales.overDeliveryAllowed` = `true` and enough stock: SO [A, 100]; Deliver
  110 -> posted; `deliveredBaseQuantity == 110`, outstanding 0, `deliveryStatus == "full"`,
  `reservedQuantity` of the pair 0. After Set to `false`, Deliver 1 more -> refused by
  (`QUANTITY_EXCEEDS_ORDER`, `sales.overDeliveryAllowed`, `false`).
- AC-65 The two rules are separate: with only `purchase.overReceiptAllowed` = `true`, a delivery above its
  sales order is still refused. With `sales.overDeliveryAllowed` = `true`, a delivery above its order and
  above stock -> `INSUFFICIENT_STOCK` (R25).

`sales.reservedStockProtected`
- AC-70 Set to `true`. Receive 10 of A into W1; SO [A, 8] (Available == 2). Issue 3 of A from W1 ->
  refused by (`STOCK_RESERVED`, `sales.reservedStockProtected`, `true`), key `lines[0].quantity`; nothing
  posted, no number consumed. Issue 2 -> posted, Available == 0.
- AC-71 After AC-70: Deliver (1, 8) against the order -> posted; Stock == 0.
- AC-72 A transfer of 3 A from W1 to W2 in the state of AC-70 (before the issue of 2) -> `STOCK_RESERVED`;
  of 2 -> posted. Stock in W2 and orders for W2 play no part.
- AC-73 Reversal: Receive 10 (document D), SO [A, 8], then reverse D -> `STOCK_RESERVED` with the keys of
  D's lines; D stays `posted`. After the order is closed, reversing D -> `201`.
- AC-74 Between orders (E7): SO1 [A, 10] and SO2 [A, 10] with stock 10; Deliver 10 for SO2 -> posted;
  Deliver 10 for SO1 -> `INSUFFICIENT_STOCK` with `rules` naming `stock.negativeStockAllowed`.
- AC-75 Already negative (E8): Available −5 from orders beyond stock; Receive 1 -> posted; Issue 1 ->
  `STOCK_RESERVED`; a count of A in W1 with a lower counted quantity -> posted.
- AC-76 With `false` (after reset) the issue of 3 of AC-70 posts.
- AC-77 Independent of negative stock: `stock.negativeStockAllowed` = `true` and
  `sales.reservedStockProtected` = `true`, stock 0, SO [A, 5]: Issue 1 -> `STOCK_RESERVED`; after the order
  is closed, Issue 1 -> posted (stock −1).
- AC-78 Confirming is unaffected: with `true` and stock 0, SO [A, 5] confirms.

`purchase.partnerRequired` and `sales.partnerRequired`
- AC-80 Default (`true`): `POST /purchase-orders` without `supplierId`, and with `"supplierId": null` ->
  each refused by (`VALIDATION_FAILED`, `purchase.partnerRequired`, `true`) with key `supplierId`; nothing
  created. `PUT` of a draft with `"supplierId": null` -> the same; the draft is unchanged. The same three
  for a sales order with `customerId` and `sales.partnerRequired`.
- AC-81 Set(`purchase.partnerRequired`, `false`). `POST /purchase-orders` without `supplierId` -> `201`
  with `supplier == null` (the property is present); with `"supplierId": null` -> `201` likewise; `GET` by
  id and the list item show `supplier == null`. With `SUP` -> `201` with the supplier, as before.
- AC-82 `false`, the partner is optional, not unchecked (E13): `supplierId` a random uuid -> `409`
  `REFERENCE_NOT_FOUND`; `CUS` -> `409` `PARTNER_ROLE_MISSING`; an inactive supplier ->
  `REFERENCE_INACTIVE`; all with key `supplierId` and without `rules`. `"abc"` -> `400` without `rules`. A
  `PUT` without the property `supplierId` -> `400` with key `supplierId`, without `rules`.
- AC-83 `false`, replace: a draft with `SUP` replaced with `"supplierId": null` -> `200`, `supplier ==
  null`; replaced again with `SUP` -> `200` with the supplier.
- AC-84 `false`, an order without a supplier lives (R35): PO [A, 10] without supplier; confirm -> `200`
  with `number == "PO-000001"`; Incoming(A, W1) == 10; Receive (1, 10) against it -> posted, and the
  receipt's `partner == null`; the order is `full`; close and reopen -> `200`. Reversing the receipt ->
  `201`; the reversing document's `partner == null`.
- AC-85 `false`, a linked document cannot name a partner the order does not have (R36): a receipt linked to
  the confirmed order of AC-84 with `partnerId: SUP` -> `409` `ORDER_MISMATCH` with key `partnerId`; with
  `partnerId: null` and without `partnerId` -> `201` with `partner == null`.
- AC-86 `false`, lists: `GET /purchase-orders` contains the order without a supplier;
  `GET /purchase-orders?supplierId=SUP` does not and contains the orders of `SUP`.
- AC-87 Mirrored for sales: Set(`sales.partnerRequired`, `false`); SO [A, 5] without `customerId` -> `201`,
  `customer == null`; confirm -> `200`; `reservedQuantity` of (A, W1) == 5; with stock, Deliver (1, 5) ->
  posted, the delivery's `partner == null`.
- AC-88 The two rules are separate: with only `purchase.partnerRequired` = `false`, a sales order without
  `customerId` is still refused by (`VALIDATION_FAILED`, `sales.partnerRequired`, `true`); and the reverse.
- AC-89 Back to `true` (E14, R37): a draft and a confirmed purchase order without a supplier exist; after
  Set(`purchase.partnerRequired`, `true`) both read unchanged (`updatedAt` included); the draft confirms
  (`200`, numbered); the confirmed one is received against, closed and reopened; replacing a draft without a
  supplier with its own body -> refused by (`VALIDATION_FAILED`, `purchase.partnerRequired`, `true`); a new
  order without a supplier -> refused likewise.

Existing data
- AC-91 After every Set and reset in AC-50 to AC-89, a document, order, ledger and stock-on-hand read before
  the change is JSON-equal to the same read after it (checked for one Set of each rule).

Timing and concurrency
- AC-95 Read-your-writes: 20 times in a row — Set(`stock.negativeStockAllowed`, `true`), post an issue of 1
  from an empty pair (expect posted), Set to `false`, post an issue of 1 (expect `INSUFFICIENT_STOCK`).
  No iteration differs.
- AC-96 Stock 5; 10 issues of 1 posted in parallel while one request Sets `stock.negativeStockAllowed` to
  `true`: every issue is `200` or `INSUFFICIENT_STOCK` whose `rules` value is `false`; none is `500`;
  Stock == 5 − (number posted); Balanced; verify empty; numbers of the posted issues are gapless.
- AC-97 Two parallel Sets of one rule in a new tenant, one to `true` and one to `false`: both `200`; the
  rule has one of the two values; the history has both changes, and the newest one's `newValue` is the
  rule's `value`.

Tenant isolation
- AC-100 Tenant X Sets all six rules to the value that is not the default. In tenant Y: `GET /rules` shows
  six defaults with `source == "default"`; `GET /rule-changes` -> empty; the five refusals of AC-40 still
  occur, their `rules` showing the defaults.
- AC-101 The same through tools: `rule_list` and `rule_change_list` with Y's key show nothing of X.

MCP
- AC-110 Through tools only, in a new tenant: `rule_list` -> 6 rules, JSON-equal to `GET /rules`;
  `rule_get` `{ key: "sales.reservedStockProtected" }` -> the rule; `rule_set`
  `{ key: "stock.negativeStockAllowed", value: true }` -> the rule with `source == "tenant"`, `updatedBy`
  the MCP request's key; `stock_document_post` of an issue from an empty pair -> posted; `rule_reset` ->
  default; `rule_change_list` -> two changes.
- AC-111 Tool errors: `rule_set` with value `"yes"`, with value `1` and without `value` ->
  `VALIDATION_FAILED`, key `value`; `rule_get` `{ key: "nope" }` -> `NOT_FOUND`; `rule_get` `{}` ->
  `VALIDATION_FAILED`, key `key`; `rule_set` with an extra argument -> `VALIDATION_FAILED` with that
  argument's key.
- AC-112 A tool error carries `rules`: `stock_document_post` of an issue above stock -> error
  `INSUFFICIENT_STOCK` whose JSON has `rules` equal to the HTTP response's `rules` for the same case; with
  reserved stock protected, -> `STOCK_RESERVED` likewise; `purchase_order_create` without `supplierId` ->
  `VALIDATION_FAILED` with key `supplierId` and `rules` naming `purchase.partnerRequired`.
- AC-113 Parity: for one Set, one reset and one refused posting, the HTTP body and the tool result are
  JSON-equal (timestamps and ids aside).
- AC-114 The partner through tools: after `rule_set` `{ key: "purchase.partnerRequired", value: false }`,
  `purchase_order_create` without `supplierId` -> an order with `supplier == null`; `purchase_order_update`
  without `supplierId` -> `VALIDATION_FAILED`, key `supplierId`, no `rules`. The input schema of
  `purchase_order_create` and `sales_order_create` does not list `supplierId` / `customerId` in `required`,
  before and after the `rule_set` (the schema does not depend on a rule).

Unchanged
- AC-120 After the criteria above, in every tenant used: Balanced, and verify is empty.

## 11. Notes for the tester and the builder

Tester
- **Restarting from the first text of this spec: read section 13 first.** Every rule key changed, every
  value is now a boolean, two rules are gone and two are new.
- The weight is on four things: defaults reproduce the built system (AC-40–AC-44); each rule at its other
  value (AC-50–AC-89); a change touches nothing stored and applies at once (AC-89–AC-97); errors name the
  rule (AC-42, the "refused by" form everywhere).
- One tenant per test is not a convenience here but a requirement: a rule set in a shared tenant changes
  other tests' results.
- Do not pin `name` or `description` texts. Do pin keys and defaults (AC-20): that list is an inventory
  test (architecture §9) and later specs add to it.
- The approved changes to earlier tests are exactly those of AC-01. If an earlier test fails for another
  reason, write it in `docs/questions/012-q.md` before changing it.
- AC-02 to AC-06 are the builder's.

Builder
- Domain: rule definitions (key, group, name, description, default) and a registry that lists them; the
  six checks as functions that take the boolean. Application: the port `IRules` (the value of a definition
  for the current tenant) and the five operations. Infrastructure: the two tables, the port's
  implementation — one query for all of a tenant's values per operation, no cache across requests
  (ADR-0020, decision 10).
- Do not build value types. A rule's value is a `bool`; there is no `type`, no `allowed`, no validation
  beyond "is a JSON boolean".
- Read rules after the per-tenant lock in every operation that takes it; Set and reset take the lock. If an
  operation validates its body before taking the lock, the partner rule (R33) may be read then — R14 is
  about the operation seeing one consistent set, and that rule is not read again later.
- `AppError` gains the rules that refused; the HTTP mapping and the MCP mapping each write `rules` in one
  place. An error that names no rule serialises without the member.
- R17 in the form "Δ < 0 and Q + Δ < 0" replaces the existing sufficiency comparison everywhere it is made
  (issue, transfer source, reversal); do not keep a second copy for reversals.
- R23 changes three computations (`outstandingBaseQuantity`, the status, and through them incoming and
  reserved): use `max(0, …)` and `>=`; under the default nothing differs.
- R27 needs the pair's reserved quantity before and after inside the posting transaction; the delivery's own
  effect on its order's outstanding quantity is part of "after".
- The partner of an order: the column and the navigation become nullable; the supplier / customer stage of
  create, replace and confirm (009/R10, R12) runs only when there is one; a linked document copies the
  order's partner, `null` included (011a/R8). The limits "6 decimals" and "200 lines" stay where they are —
  they are platform bounds, not rules; do not move them onto the registry.
- Anything unclear or contradictory: `docs/questions/012-q.md`, then continue with the rest.

## 12. Decisions behind this spec

Owner's decisions (2026-10-10; ADR-0020): a rule is a boolean; every tenant key may change rules (S1, R10);
negative stock is each tenant's own decision (6.4).

Architect's defaults — each stands until the owner says otherwise; the rule that carries it is named.

| # | Question | Default | Rule |
|---|---|---|---|
| 1 | When does a change apply? | From the next operation; nothing stored is changed or re-checked | R13, R15 |
| 2 | Negative stock allowed, then refused again | Negative balances stay; movements that raise them pass, movements that lower them are refused | R21 |
| 3 | Over-receipt / over-delivery allowed | Without limit — a rule is yes or no | R22 |
| 4 | An order received above 100 % | Outstanding is 0, status `full`; it still has to be closed by hand | R23 |
| 5 | What "reserved stock is protected" protects | Reserved stock against manual issues, transfers and reversals — not one order against another; no allocation | R27, R28 |
| 6 | Is a stock count blocked by reservation or by the negative-stock rule? | No — it states what is there | R29 |
| 7 | Partner not required: is a partner that *is* named still checked? | Yes — existence, activity, role, as before | R34 |
| 8 | A draft order without a partner, after the partner became required | It still confirms; only its next save is judged | R37 |
| 9 | A receipt or delivery against an order without a partner | Has no partner; naming one is `ORDER_MISMATCH` | R36 |
| 10 | Can orders without a partner be listed on their own? | No filter for it | R35 |
| 11 | Decimals of a quantity (6) and lines per document (200) | Platform bounds, fixed; not rules | 6.1 |
| 12 | Setting a rule to the value of its default | Counts as the tenant's own value and is kept if we later change the default | R7 |

## 13. What changed against the first text of this spec (for the tester's restart)

The first text (commit `67a8d96`) had typed rules (`choice`, `integer`, `decimal`). Everything below
replaces it; tests written against the first text must be rewritten, not adjusted.

**Rules**

| First text | Now |
|---|---|
| `stock.negativeStock`: `"refuse"` (default) / `"allow"` | `stock.negativeStockAllowed`: `false` (default) / `true` |
| `purchase.overReceiptPercent`: decimal 0–1000, default 0 | `purchase.overReceiptAllowed`: `false` (default) / `true`; `true` has **no limit** |
| `sales.overDeliveryPercent`: decimal 0–1000, default 0 | `sales.overDeliveryAllowed`: `false` (default) / `true`; no limit |
| `sales.reservation`: `"inform"` (default) / `"block"` | `sales.reservedStockProtected`: `false` (default) / `true` |
| `quantity.decimals`: integer 0–6, default 6 | **removed** — six decimals is a fixed platform bound; its refusal has no `rules` |
| `document.maxLines`: integer 1–1000, default 200 | **removed** — 200 lines is a fixed platform bound; its refusal has no `rules` |
| — | **new** `purchase.partnerRequired`: `true` (default) / `false` |
| — | **new** `sales.partnerRequired`: `true` (default) / `false` |

Still six rules; `total == 6`; the literal order of AC-20 is new.

**Value shapes**
- Every `value`, `default`, `oldValue`, `newValue` and every `rules[].value` is a JSON boolean. Strings,
  numbers and `null` are invalid values (`400`, key `value`).
- The rule object lost `type` and `allowed`; it has exactly nine properties (section 4).
- `rule_set`'s `value` argument is a boolean.

**Endpoints and tools**
- The five rule routes and the five rule tools are unchanged in path, method, name and count (62 tools).
  `GET /rules` filters (`search`, `group`, `source`) and `GET /rule-changes` are unchanged.
- **Changed existing operations** (new): create of a purchase / sales order accepts an omitted or `null`
  `supplierId` / `customerId`; replace requires the property and accepts `null`; `supplier` / `customer` in
  every order representation may be `null`; a stock document linked to such an order has `partner == null`.
  Tool schemas change accordingly (section 5).
- Error code `STOCK_RESERVED` and the `rules` member are unchanged in shape.

**Rules of this spec**
- 6.2–6.4 and 6.6: same behaviour, boolean values (R6 is new: only `true` / `false`).
- 6.5: no percentage, no limit formula, no rounding (old R22, E11 are gone).
- Old 6.7 (`quantity.decimals`, `document.maxLines`; R32–R34) is gone; new 6.7 is the partner rules
  (R32–R37). "Errors name the rule" is now R38–R40.

**Acceptance criteria**
- Unchanged in substance, new keys and values: AC-10–AC-12, AC-21, AC-30, AC-32, AC-34–AC-36, AC-41,
  AC-50–AC-54, AC-70–AC-78, AC-91, AC-95–AC-97, AC-100, AC-101, AC-110, AC-113, AC-120.
- Rewritten: AC-01 (approved changes to earlier tests), AC-03, AC-05, AC-06, AC-20, AC-22, AC-31, AC-33,
  AC-37, AC-40, AC-42, AC-43, AC-44, AC-60–AC-65, AC-111, AC-112.
- Removed: old AC-80–AC-83 (decimals, maximum lines) and AC-90 (drafts under a narrower limit).
- New: AC-80–AC-89 (partner required), AC-114.
