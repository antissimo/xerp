# Rules: what a tenant can change and what nobody can

Owner of this file: the architect. Mechanism: ADR-0020. This file is the **catalogue of configurable rules**
and the **list of invariants**, and — in section 4 — the inventory of every rule and validation of specs
001–011a, each classified. A spec that adds a rule adds it here (architecture §11).

Owner requirement (2026-10-10): every validation and business rule is configurable per tenant; we ship the
default. Because the owner's words were "every rule", what is **not** configurable is listed first and
explicitly (section 2) and is his to approve (section 5).

## 1. How to read this file

- **CONFIGURABLE** — a rule with a key, a type, a default and allowed values (ADR-0020). The default is what
  specs 001–011a do today: a tenant that sets nothing sees no change.
- **INVARIANT** — cannot be switched off without breaking integrity, security or the contract. Referred to as
  I1–I12.
- **Batch** — the spec that moves the rule onto the registry. Until that spec is merged the rule is still a
  fixed check with the default's behaviour. `012` is written; `013`, `014`, `017`, `021` are roadmap rows.
  **feature** means: the rule is a business choice, not an invariant, but its other value is behaviour that
  does not exist yet; it becomes a rule with the spec that builds it.
- Sources are written `005/R15` (spec 005, rule R15), `arch §5` (architecture), `ADR-0012`.

## 2. Invariants — not configurable

An invariant is one of four things: **security**, **integrity of the records**, **the shape of the contract**,
or a **definition** (what a thing *is*, as opposed to what is allowed).

| # | Invariant | Kind | Why it cannot be a setting |
|---|---|---|---|
| I1 | **Tenant isolation.** Every row belongs to one tenant; the tenant comes only from the credential; another tenant's record is indistinguishable from a missing one (`404`); references and uniqueness are per tenant. | security | A tenant that could loosen it would loosen it for the others. |
| I2 | **Authentication and key secrecy.** Every route needs a key; only its hash is stored; the plaintext is shown once and never through MCP; admin and tenant credentials are separate; a revoked key stays revoked and is never deleted; a key cannot revoke itself, so a tenant always keeps an active key. | security | A setting that weakens authentication is itself reachable only through authentication; self-revocation locks the tenant out for good. |
| I3 | **Attribution.** Ids, timestamps and actor fields are set by the server, never by the caller; every write, posting, confirmation and rule change records the key that did it. | integrity | "The agent is a user" only holds if every act is somebody's. |
| I4 | **Posted is final.** The ledger is append-only; a posted stock document and its number never change; a mistake is corrected by a reversing document, at most once per original, and a reversing document cannot be reversed; original and reversal sum to zero. | integrity | An editable ledger is not a ledger; every later number (balance, value, tax) rests on it. |
| I5 | **Only documents move stock, and the numbers agree.** Posting is atomic; stock on hand == sum of the ledger == stored balance, per article and warehouse, at every moment; a transfer changes no article's total; the ledger is in base units. | integrity | These are equalities, not policies; with one of them off the system reports stock it cannot explain. |
| I6 | **References hold.** A referenced record must exist in the tenant; a record that something references cannot be deleted (`IN_USE`), only retired; an article's `type` and base unit, and a conversion a draft uses, cannot change under the documents that depend on them. | integrity | Otherwise a posted quantity loses its unit, or a document its article. |
| I7 | **Identity.** A master has a code, unique in its tenant and resource, compared case-insensitively; a document number is unique per tenant. | integrity | `by-code` and `by-number` must find one record. (The *format* of codes and numbers is configurable.) |
| I8 | **Definitions.** What each document type is and its states (a draft has no effect and reserves nothing; an order is fulfilled only while `confirmed`; `closed` means no more); a document has at least one line and a line moves more than zero (a count states zero or more, one statement per article); a transfer has two different warehouses; a `service` article has no stock; the base unit has factor 1 and a line's unit must be a unit of its article; a document linked to an order has that order's partner and its lines the order lines' articles; received, outstanding, incoming, reserved and available quantities are computed, never set. | definition | Switching one off does not relax a rule, it makes a word mean nothing: a "transfer" from a warehouse to itself, a "count" with two answers. |
| I9 | **The shape of the API contract.** JSON shapes and types; unknown properties and query parameters are refused; text is trimmed, empty optional text is `null`, single-line text has no control characters; the error document, its codes and the order of checks; the list envelope and paging (`limit` 1–500, default 50); a replace carries every field; one MCP tool per operation with the same fields and errors. | contract | Separately built clients (CLI, web, agents) are written against it; a contract that differs per tenant is not one. |
| I10 | **Platform bounds.** Exact decimals, never floating point; storage precision (6 decimals; quantities to 999999999.999999; factors to 999999.999999; line amounts to 9999999999.99) and column lengths — these are the *ends of the allowed range* of the rules that sit under them; a request body of at most 1 MB; a search text of at most 100 characters; the admin key and the MCP origin allow-list (settings of the operator, not of a tenant). | security / storage | They protect the shared service and the shared schema; one tenant cannot be given a wider column. |
| I11 | **Structure the owner decided** (ADR-0019): every tenant has exactly one default warehouse and it is active (so it cannot be deactivated or deleted while default); every document has a warehouse in its header. | owner decision | Listed here because it is the owner's own rule; he can reopen it. *What an omitted `warehouseId` means* is configurable. |
| I12 | **The rule mechanism itself** (ADR-0020): a value must be within the rule's allowed values; a change is attributed and kept in the history; a change applies to new writes and rewrites nothing. | integrity | A rule about rules has to stand still. |

Not rules at all, so in neither class: **initial data** a tenant can change afterwards (the first key is
named `initial`; the first warehouse is `CENTRAL` / "Central warehouse") and **what nothing forbids** (a
master can always be renamed, re-coded and deactivated; an order can be closed whatever was received).

## 3. Catalogue of configurable rules

Every key, with the default that reproduces today's behaviour. `<m>` stands for each of `uom`, `article`,
`partner`, `warehouse`.

### Batch 012 — stock and order policies (spec 012, written)

| Key | Type | Default | Allowed | Decides |
|---|---|---|---|---|
| `stock.negativeStock` | choice | `refuse` | `refuse`, `allow` | Whether a posting or reversal may take stock on hand below zero. |
| `purchase.overReceiptPercent` | decimal | `0` | 0–1000, 2 decimals | How far above the ordered quantity an order line may be received, in percent. |
| `sales.overDeliveryPercent` | decimal | `0` | 0–1000, 2 decimals | The same for deliveries against a sales order line. |
| `sales.reservation` | choice | `inform` | `inform`, `block` | Whether stock reserved by confirmed sales orders can be taken by other movements. |
| `quantity.decimals` | integer | `6` | 0–6 | Decimal places a caller may give in a line's `quantity`. |
| `document.maxLines` | integer | `200` | 1–1000 | Most lines on a stock document or an order. |

### Batch 013 — master data: formats, lengths, required fields, uniqueness (roadmap)

| Key | Type | Default | Allowed | Decides |
|---|---|---|---|---|
| `<m>.code.pattern` (4 keys) | pattern | `^[\p{L}\p{N}._-]+$` | any linear-time pattern, ≤ 200 chars | Which characters a code may have. |
| `<m>.code.maxLength` (4) | integer | `50` | 1–50 | Longest code. |
| `<m>.code.changeable` (4) | boolean | `true` | — | Whether a replace may change the code. |
| `<m>.name.maxLength` (4) | integer | `200` | 1–200 | Longest name. |
| `<m>.name.unique` (4) | boolean | `false` | — | Whether two records may share a name. |
| `apiKey.name.maxLength` | integer | `100` | 1–100 | Longest API key name. |
| `article.description.maxLength` | integer | `2000` | 1–2000 | Longest description. |
| `article.description.required` | boolean | `false` | — | Whether an article must have a description. |
| `partner.role.required` | boolean | `true` | — | Whether a partner must be a customer or a supplier (or both). |
| `partner.taxId.required` | boolean | `false` | — | Whether a partner must have a tax id. |
| `partner.taxId.unique` | boolean | `false` | — | Whether two partners may share a tax id. |
| `partner.taxId.maxLength` | integer | `50` | 1–50 | Longest tax id. |
| `partner.taxId.pattern` | pattern | none (`null`) | any linear-time pattern, or `null` | Form of a tax id; `null` = any text. |
| `partner.<f>.required`, `warehouse.<f>.required` for the six address fields (12) | boolean | `false` | — | Whether that address field must be filled. |
| `address.line.maxLength` | integer | `200` | 1–200 | Longest `addressLine1` / `addressLine2`. |
| `address.postalCode.maxLength` | integer | `20` | 1–20 | Longest postal code. |
| `address.city.maxLength`, `address.region.maxLength` | integer | `100` | 1–100 | Longest city / region. |
| `address.countryCode.pattern` | pattern | `^[A-Z]{2}$` | any linear-time pattern (the field holds 2 characters) | Form of a country code. |

### Batch 014 — documents: dates, references, partner, counts, prices, order life cycle (roadmap; may be split in two when written)

| Key | Type | Default | Allowed | Decides |
|---|---|---|---|---|
| `references.inactiveOnAssign` | choice | `refuse` | `refuse`, `allow` | Whether an inactive record can be newly referenced (`REFERENCE_INACTIVE` on save). |
| `posting.inactiveMasters` | choice | `refuse` | `refuse`, `allow` | Whether posting and confirmation need the warehouse, articles and partner to be active. |
| `stock.reversal.inactiveMasters` | choice | `allow` | `allow`, `refuse` | The same question for a reversal. |
| `document.date.maxDaysPast` | integer | none (`null`) | 0–36500, or `null` | How far back a document or order date may lie. |
| `document.date.maxDaysFuture` | integer | none (`null`) | 0–36500, or `null` | How far ahead. |
| `stock.reversal.dateBeforeOriginal` | choice | `refuse` | `refuse`, `allow` | Whether a reversal may be dated before its original. |
| `order.dueDate.beforeOrderDate` | choice | `refuse` | `refuse`, `allow` | Whether `expectedDate` / `requestedDate` may precede `orderDate`. |
| `purchase.expectedDate.required`, `sales.requestedDate.required` | boolean | `false` | — | Whether the order must carry that date. |
| `document.reference.maxLength` | integer | `100` | 1–100 | Longest `reference`. |
| `document.reference.required` | boolean | `false` | — | Whether a document must carry a `reference`. |
| `document.note.maxLength` | integer | `2000` | 1–2000 | Longest `note`. |
| `document.repeatedArticle` | choice | `allow` | `allow`, `refuse` | Whether one article may be on several lines (a count: never, I8). |
| `document.warehouse.whenOmitted` | choice | `default` | `default`, `refuse` | Whether an omitted `warehouseId` on create means the default warehouse or is an error. |
| `stock.receipt.partner`, `stock.issue.partner` | choice | `optional` | `optional`, `required` | Whether an unlinked receipt / issue must name a partner. |
| `purchase.supplier.roleRequired`, `sales.customer.roleRequired`, `stock.partner.roleRequired` | boolean | `true` | — | Whether the partner must have the role the document needs (`PARTNER_ROLE_MISSING`). |
| `purchase.receipt.otherWarehouse`, `sales.delivery.otherWarehouse` | choice | `refuse` | `refuse`, `allow` | Whether a linked receipt / delivery may use a warehouse other than its order's. |
| `stock.count.outdated` | choice | `refuse` | `refuse`, `adjust` | A count whose book quantity is no longer current: refused (`COUNT_OUTDATED`), or posted against the stock at posting. |
| `conversion.factor.decimals` | integer | `6` | 0–6 | Decimal places of a unit conversion factor. |
| `quantity.rounding` | choice | `halfAwayFromZero` | `halfAwayFromZero`, `halfEven`, `up`, `down` | Rounding of a converted (base) quantity. |
| `price.decimals` | integer | `6` | 0–6 | Decimal places of a unit price. |
| `price.zero` | choice | `allow` | `allow`, `refuse` | Whether a unit price of 0 is accepted. |
| `amount.decimals` | integer | `2` | 0–2 | Decimal places of a line amount (more than 2: with multi-currency). |
| `amount.rounding` | choice | `halfAwayFromZero` | `halfAwayFromZero`, `halfEven` | Rounding of a line amount. |
| `order.reopen` | choice | `allow` | `allow`, `refuse` | Whether a closed order can be reopened. |
| `order.autoClose` | boolean | `false` | — | Whether an order closes itself when every line is fulfilled. |
| `sales.confirm.beyondAvailable` | choice | `allow` | `allow`, `refuse` | Whether a sales order can be confirmed for more than is available. |

### With other specs

| What | Spec | Note |
|---|---|---|
| Document numbers: prefix, digits, yearly restart, gapless or not, per document type | 017 Number series | Today `SR-`, `SI-`, `ST-`, `SC-`, `PO-`, `SO-` + 6 digits, gapless. The series spec defines its keys on this registry. Uniqueness stays I7. |
| Who may do what: manage keys, post, confirm, change rules, verify and rebuild balances | 021 Permissions | Today: every key everything. Permissions are per key, not a value per tenant; that spec decides their form. |
| A confirmed order can be changed | feature (order amendment, "Later") | Today immutable; close and make a new one. |
| Part of a posted document can be reversed | feature (returns, "Later") | Today whole and final. |
| `service` articles on orders | feature (018 Invoices) | Today only `stock` articles. |
| A draft reserves stock or order quantity | feature ("Later") | Today a draft has no effect (I8 as long as that is so). |
| One tenant currency with other precision; several addresses per partner | feature ("Later") | — |

## 4. Inventory — every rule of specs 001–011a

One row per rule or validation. "Key / invariant" names the rule of section 3 or the invariant of section 2.

### Transport, security, API (001, 003, architecture §3–§7)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 001/S1–S5, 003/S1, S3–S5 | Bearer key on every route; hash only; plaintext once; no key in a URL; nothing secret in logs | INVARIANT | I2 | Security. |
| 001/S3–S4 | Admin key from configuration, ≥ 32 characters; admin and tenant routes mutually `403` | INVARIANT | I2, I10 | The operator's credential, not a tenant's. |
| 003/S2 | MCP `Origin` allow-list | INVARIANT | I10 | Operator setting; protects browsers of all tenants. |
| arch §3, every spec §8 | Tenant from the credential; other tenant = `404` / `REFERENCE_NOT_FOUND` | INVARIANT | I1 | Security. |
| 003/R5–R9 | Revocation permanent; keys never deleted; no self-revocation; revoked key refused at once | INVARIANT | I2, I3 | Lock-out and attribution. |
| 003/R2, R11 | `actorType` is `human` or `agent`, fixed; restricts nothing | INVARIANT | I3 | It is the attribution label. What a key *may do* is permissions (021). |
| 003/S7, 011/R27, 011a/S1, ADR-0003 | Every key may manage keys, post, rebuild, change rules | CONFIGURABLE | 021 Permissions | Role requirements are that spec. |
| 001/R13 | Key format `xerp_` + 43 characters | INVARIANT | I2 | Security parameter. |
| 003/R1 | API key name 1–100, need not be unique | CONFIGURABLE | `apiKey.name.maxLength` (013) | A length. (A name is required: I9.) |
| 001/R10, R11, R7 | Ids and audit fields set by the server | INVARIANT | I3 | Attribution. |
| 001/R10, R14, R15, E4 | Unknown property / parameter refused; names case-sensitive; duplicates refused; wrong JSON type refused | INVARIANT | I9 | Contract: a typo must be an error for every client. |
| 001/R16, R4 (control characters), 002/R3 | No input causes `500`; single-line text without control characters; multi-line text only LF, CR, TAB | INVARIANT | I9, I10 | Storage cannot hold NUL; "single line" is the field's shape. |
| 001/R1, ADR-0011/7 | Trim; empty optional text is `null` | INVARIANT | I9 | Normalisation is the contract's shape. |
| 001/R5, 002/R11, 004/R7, 011a/R3 | Replace carries every field | INVARIANT | I9 | Contract. |
| 001/R9, arch §5 | List envelope; `limit` 1–500 default 50; `offset` ≥ 0; ordering; `search` ≤ 100, substring, wildcards literal | INVARIANT | I9, I10 | Contract and protection. |
| arch §5–§6, ADR-0004 | Error document, codes, order of checks, `404` for unknown path and method | INVARIANT | I9 | Contract. |
| cleanup 001/7 | Body ≤ 1 MB (`413`) | INVARIANT | I10 | Protects the shared service. |
| 003/R12–R17, ADR-0009 | MCP: a tool is one operation; exactly one of `id` / `code`; JSON types; errors as tool errors | INVARIANT | I9 | Contract. |
| 001/R12, 011/R1 | A new tenant gets a key `initial` and a warehouse `CENTRAL` | — | initial data | The tenant renames them. |

### Masters (001, 002, 004, 007, 011)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 001/R2, 002/R1, 004/R1 | Code: letters, digits, `.` `_` `-` | CONFIGURABLE | `<m>.code.pattern` (013) | A format. |
| same | Code: 1–50 characters | CONFIGURABLE | `<m>.code.maxLength` (013) | A length; 50 is the column (I10). |
| 001/E1 | Code required | INVARIANT | I7 | Identity. |
| 001/R3, 002/R1, 004/R1 | Code unique per tenant and resource, case-insensitive; four namespaces | INVARIANT | I7 | Identity; enforced by an index shared by all tenants. |
| 001/R6, 002/R12, 004/R8 | A replace may change the code | CONFIGURABLE | `<m>.code.changeable` (013) | A policy. |
| 001/R4, 002/R2, 004/R2 | Name 1–200 | CONFIGURABLE | `<m>.name.maxLength` (013) | A length. (Required: I9 — summaries carry `name`.) |
| 001, 004 (implicit) | Names need not be unique | CONFIGURABLE | `<m>.name.unique` (013) | A policy. |
| 001/R2 (tenant code, name) | Tenant code and name format | INVARIANT | I10 | Set by the operator with the admin key; no tenant exists yet to configure it. |
| 001/R5, 002/R10, 004/R6, R14 | `isActive` defaults to `true`, role flags to `false`, on create | INVARIANT | I9 | Defaults of the contract. |
| 002/R3 | Description ≤ 2000, optional | CONFIGURABLE | `article.description.maxLength`, `.required` (013) | A length, a required field. |
| 002/R4, R7 | `type` is `stock` or `service`; base unit required for both | INVARIANT | I8, I6 | Definition; a quantity needs a unit. |
| 002/R6, 004, 005/R3, R5, ADR-0008 | A reference must exist in the tenant | INVARIANT | I6, I1 | Integrity. |
| 002/R9, 005/R3, R5, 007/R4, R12, 011a/R5 | An inactive record cannot be newly referenced; a kept reference stays valid | CONFIGURABLE | `references.inactiveOnAssign` (014) | A policy about retired records. |
| 002/R16, 005/R25, 007/R8, R10, 009/R37, 011a/R15 | A referenced record cannot be deleted (`IN_USE`) | INVARIANT | I6 | Integrity. |
| 005/R26, 007/R9, 009/R38 | `type` and base unit frozen once used or once conversions exist | INVARIANT | I6 | Posted quantities would change meaning. |
| 004/R15 | A partner is a customer or a supplier (at least one) | CONFIGURABLE | `partner.role.required` (013) | A policy (ADR-0011 default). |
| 004/R16 | Tax id not unique | CONFIGURABLE | `partner.taxId.unique` (013) | A policy. |
| 004/R3 | Tax id optional, ≤ 50, no format | CONFIGURABLE | `partner.taxId.required`, `.maxLength`, `.pattern` (013) | Required field, length, format. |
| 004/R3, R5 | Address fields optional, independent, ≤ 200 / 20 / 100 | CONFIGURABLE | `<…>.required`, `address.*.maxLength` (013) | Required fields, lengths. |
| 004/R4 | Country code two upper-case letters, not checked against a list | CONFIGURABLE | `address.countryCode.pattern` (013) | A format. |
| 004/R9 | Booleans are JSON booleans | INVARIANT | I9 | Contract. |
| 007/R2 | Factor > 0 | INVARIANT | I8 | Definition of a conversion. |
| 007/R2 | Factor ≤ 6 decimals | CONFIGURABLE | `conversion.factor.decimals` (014) | A precision; the maximum is I10. |
| 007/R5, R1 | No conversion for the base unit; base unit has factor 1 | INVARIANT | I8 | Definition. |
| 007/R7 | A factor can change at any time | — | nothing forbids | Posted lines keep their own (I4). |
| 011/R2, R4–R6 | Exactly one active default warehouse; it cannot be deactivated or deleted; set-default needs an active target | INVARIANT | I11 | Owner's structure (ADR-0019). |
| 011/R3, R7 | The default is otherwise ordinary; a former default can be retired | — | nothing forbids | — |

### Stock documents (005–008, 011, 011a)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 005/R1, 006/R1, 008/R1 | Types `receipt`, `issue`, `transfer`, `count`; the type never changes | INVARIANT | I8 | Definition. |
| 005/R2 | `documentDate` required, a real `YYYY-MM-DD` date | INVARIANT | I9 | Contract. |
| 005/R2 | Any date, past or future | CONFIGURABLE | `document.date.maxDaysPast`, `.maxDaysFuture` (014) | A date policy; default: no limit. |
| arch §5, 011/R8 | Every document has a warehouse | INVARIANT | I11 | Owner's structure. |
| 011/R8–R10 | Omitted `warehouseId` on create = the default (linked: the order's) | CONFIGURABLE | `document.warehouse.whenOmitted` (014) | A convenience a tenant may want off. |
| 005/R4 | At least one line | INVARIANT | I8 | A document without lines states nothing. |
| 005/R4, 008/R2, 009/R5 | At most 200 lines | CONFIGURABLE | `document.maxLines` (**012**) | A limit; ceiling 1000 is I10. |
| 005/R4 | The same article on several lines | CONFIGURABLE | `document.repeatedArticle` (014) | A policy. |
| 008/R3 | A count names each article once | INVARIANT | I8 | Two counted quantities for one article contradict. |
| 005/R6 | Quantity > 0 (count: ≥ 0, 008/R4) | INVARIANT | I8 | A line moves something; direction comes from the type. |
| 005/R6 | Quantity ≤ 6 decimals | CONFIGURABLE | `quantity.decimals` (**012**) | A precision. |
| 005/R6 | Quantity ≤ 999999999.999999, a JSON number | INVARIANT | I10, I9 | Storage; contract. |
| 005/R7 | `reference` ≤ 100, `note` ≤ 2000, both optional | CONFIGURABLE | `document.reference.maxLength`, `.required`, `document.note.maxLength` (014) | Lengths, a required field. |
| 005/R9–R11 | Draft: no effect, replaceable, deletable; posted: immutable | INVARIANT | I8, I4 | Definition; posted is final. |
| 005/R12, R14, 006/R6, R8 | Posting is atomic; entries and signs by type; a transfer sums to zero | INVARIANT | I5 | Integrity. |
| 005/R13, 006/R5, 009/R12, 011a/R12 | Posting and confirmation need active warehouse, articles (and, unlinked, partner) | CONFIGURABLE | `posting.inactiveMasters` (014) | A policy about retired records. |
| 005/R15–R16, 006/R7, 010/R9 | **No negative stock** on issue, transfer, delivery | CONFIGURABLE | `stock.negativeStock` (**012**) | A policy (ADR-0012 default); ERPs offer both. |
| 006/R16, 008/R18 | No negative stock by reversal | CONFIGURABLE | `stock.negativeStock` (**012**) | The same rule. |
| 005/R18, 008/R13 | A receipt and a count are never refused for stock | — | nothing forbids | — |
| 005/R17, 006/R9, 008/R15, 009/R13 | Number = fixed prefix + 6 digits, gapless, per type, assigned at posting / confirmation | CONFIGURABLE | 017 Number series | Format and gaps are policy; uniqueness is I7. |
| 005/R19, 011/R19–R23 | Stock on hand = sum of the ledger = stored balance | INVARIANT | I5 | Equality. |
| 011/R24–R26 | Verify reports, rebuild repairs, neither changes the ledger | INVARIANT | I5 | They are how I5 is checked. |
| 005/R5, 009/R6 | Only `stock` articles on stock documents | INVARIANT | I8 | A service has no stock. |
| 006/R2 | A transfer's warehouses differ | INVARIANT | I8 | Definition. |
| 006/R3, 008/R1, 011a/R1 | `toWarehouseId` only on transfers; `partnerId` only on receipts and issues | INVARIANT | I8, I9 | Shape of each type. |
| 006/R11, R18–R19 | Only a posted, non-reversing document is reversed, once | INVARIANT | I4 | Net zero would break. |
| 006/R13 | Reversal is of the whole document | CONFIGURABLE | feature (returns) | A choice (ADR-0013); partial reversal is not built. |
| 006/R12 | Reversal date not before the original's | CONFIGURABLE | `stock.reversal.dateBeforeOriginal` (014) | A date policy. |
| 006/R17, 011a/R14 | Reversal does not need active masters | CONFIGURABLE | `stock.reversal.inactiveMasters` (014) | A policy; default keeps mistakes correctable. |
| 007/R12 | A line's unit is a unit of its article | INVARIANT | I8 | Without a factor there is no base quantity. |
| 007/R14 | Base quantity rounded to 6 decimals, half away from zero | CONFIGURABLE | `quantity.rounding` (014) | Rounding mode; 6 decimals is I10. |
| 007/R15 | A base quantity of 0 or above the maximum is refused | INVARIANT | I8, I10 | Moves nothing / cannot be stored. |
| 007/R17, R20 | A draft follows the current factor; a posted line keeps its own | INVARIANT | I8, I4 | Draft fixes nothing; posted is final. |
| 007/R19 | The ledger is in base units | INVARIANT | I5 | Sums need one unit. |
| 008/R6–R7 | Book quantity taken at save; difference = counted − book | INVARIANT | I8 | Definition of a count. |
| 008/R10 | An outdated count is refused (`COUNT_OUTDATED`) | CONFIGURABLE | `stock.count.outdated` (014) | A policy (ADR-0015 default). |
| 011a/R2 | Partner on a receipt / issue is optional | CONFIGURABLE | `stock.receipt.partner`, `stock.issue.partner` (014) | A required field. |
| 011a/R6, R12 | Its role: supplier on a receipt, customer on an issue | CONFIGURABLE | `stock.partner.roleRequired` (014) | A role requirement. |
| 011a/R8–R10 | A linked document has the order's partner | INVARIANT | I8 | It is what "linked" means. |

### Orders and fulfilment (009, 010)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 009/R1, 010/R1 | An order has one partner and one warehouse | INVARIANT | I8, I11 | Definition; owner's structure. |
| 009/R2, 010/R1 | Supplier must be a supplier, customer a customer | CONFIGURABLE | `purchase.supplier.roleRequired`, `sales.customer.roleRequired` (014) | A role requirement. |
| 009/R3 | `orderDate` required | INVARIANT | I9 | Contract. |
| 009/R3, 010/R1 | `expectedDate` / `requestedDate` optional, not before `orderDate` | CONFIGURABLE | `…Date.required`, `order.dueDate.beforeOrderDate` (014) | Required field; date policy. |
| 009/R6 | Only `stock` articles on orders | CONFIGURABLE | feature (018) | A gap, not a principle: services are ordered once they can be invoiced. |
| 009/R8 | Unit price ≥ 0 | INVARIANT | I8 | A negative price is a credit, another document. |
| 009/R8 | Unit price 0 allowed | CONFIGURABLE | `price.zero` (014) | A policy. |
| 009/R8 | Unit price ≤ 6 decimals | CONFIGURABLE | `price.decimals` (014) | A precision; maximum is I10. |
| 009/R9 | Line amount = quantity × price, 2 decimals, half away from zero | CONFIGURABLE | `amount.decimals`, `amount.rounding` (014) | Precision and rounding; the formula is I8. |
| 009/R9 | Line amount ≤ 9999999999.99 | INVARIANT | I10 | Storage. |
| 009/R11–R16 | States `draft -> confirmed <-> closed`; a draft orders nothing | INVARIANT | I8 | Definition. |
| 009/R14 | **A confirmed order is immutable** | CONFIGURABLE | feature (order amendment) | A choice (ADR-0016), not integrity: an order writes no ledger. |
| 009/R16 | A closed order can be reopened | CONFIGURABLE | `order.reopen` (014) | A policy. |
| ADR-0016 | Orders never close by themselves | CONFIGURABLE | `order.autoClose` (014) | A policy. |
| 009/R18–R20, 010/R5–R6 | Only a receipt links to a purchase order, only an issue to a sales order; fixed at creation; every line names an order line | INVARIANT | I8 | What a link is. |
| 009/R21.2, R24, R31 | Fulfilment only against a `confirmed` order (`ORDER_NOT_OPEN`) | INVARIANT | I8 | What `draft` and `closed` mean; the tenant reopens. |
| 009/R21.4 | Receipt / delivery only in the order's warehouse | CONFIGURABLE | `purchase.receipt.otherWarehouse`, `sales.delivery.otherWarehouse` (014) | A policy (ADR-0016 default). |
| 009/R21.4 | A line's article is its order line's | INVARIANT | I8 | Another article is another order line. |
| 009/R25, R27, R29, 010/R8 | Received / delivered, outstanding and status are computed from posted documents | INVARIANT | I8, I5 | Derived numbers. |
| 009/R26 | **Never receive more than ordered** | CONFIGURABLE | `purchase.overReceiptPercent` (**012**) | A policy (ADR-0016 default). |
| 010/R7 | **Never deliver more than ordered** | CONFIGURABLE | `sales.overDeliveryPercent` (**012**) | The same. |
| 009/R34, 010/R14 | A linked document is reversed whatever the order's status | — | nothing forbids | — |
| 010/R4 | Confirmation never refused for stock | CONFIGURABLE | `sales.confirm.beyondAvailable` (014) | A policy. |
| 010/R15–R16, 009/R35 | Reserved, incoming, available: definitions | INVARIANT | I8 | Derived numbers. |
| 010/R17 | **Reservation informs, does not block** | CONFIGURABLE | `sales.reservation` (**012**) | A policy (ADR-0017 default). |

## 5. Decisions for the owner

1. **The invariants I1–I12.** They are what "every rule" does not reach. I8 (definitions), I9 (contract
   shape) and I10 (platform bounds) are the wide ones; say if any item in them should be a setting.
2. **"However they want" means: any value of a rule we define** — not rules a tenant writes itself (no
   conditions, no expressions), and one value per tenant (not per warehouse, article or key). ADR-0020 says
   why and how both can be added later.
3. **Who may change rules.** Default: every tenant key, agents included, as for everything else until
   permissions exist. An agent refused by a rule is told its key and can switch it off; the history shows it.
   Alternative: bring the permissions spec forward.
4. **Order of work.** The retrofit is three specs (012 written; 013 masters; 014 documents) before valuation
   resumes, because the rule is "from the start". Alternative: 012 only, and move the other rules as their
   areas are touched.
5. **Tightening is not retroactive, and a replace is judged whole.** Old records stay valid; but the next
   `PUT` of an old record must satisfy the current rules.
6. **Rules whose other value is a feature** (a confirmed order can be changed; partial reversal; services on
   orders; drafts that reserve) stay fixed until that feature is specified. They are not invariants.
7. **Negative stock allowed** removes the database's own check on the balance, and valuation will have to say
   what stock below zero costs.
8. **Over-receipt and over-delivery are a percentage**, 0 to 1000; there is no "unlimited".
9. **`sales.reservation = block`** protects reserved stock from every other movement, but a stock count is
   never blocked (it states what is physically there).
10. **Our defaults never change under an existing tenant**: if we change one, existing tenants keep the old
    value as their own.
