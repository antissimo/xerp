# Rules: what a tenant can change and what nobody can

Owner of this file: the architect. Mechanism: ADR-0020. This file is the **catalogue of configurable rules**
and the **list of invariants**, and — in section 4 — the inventory of every rule and validation of specs
001–011a, each classified. A spec that adds a rule adds it here (architecture §11).

Owner requirement (2026-10-10): every validation and business rule is configurable per tenant; we ship the
default. Owner's decisions of the same day (section 5): the invariants are approved — **only business logic
is configurable** — and **a rule is a boolean**: a named yes/no switch in plain language. His example:
*"Partner required on an order: True or False."*

## 1. How to read this file

- **CONFIGURABLE** — a rule: a key, a plain-language name and a default; its value is `true` or `false` per
  tenant (ADR-0020). The name is a statement; **`true` means the statement holds**. The default is what
  specs 001–011a do today: a tenant that sets nothing sees no change.
- **INVARIANT** — cannot be switched off without breaking integrity, security or the contract, or is a bound
  of the platform. Referred to as I1–I12.
- **Batch** — the spec that moves the rule onto the registry. Until that spec is merged the rule is still a
  fixed check with the default's behaviour. `012` is written; `013`, `014`, `017`, `021` are roadmap rows.
  **feature** means: the rule is a business choice, not an invariant, but its other value is behaviour that
  does not exist yet; it becomes a switch with the spec that builds it.
- Sources are written `005/R15` (spec 005, rule R15), `arch §5` (architecture), `ADR-0012`.

## 2. Invariants — not configurable

Approved by the owner, 2026-10-10. An invariant is one of four things: **security**, **integrity of the
records**, **the shape of the contract**, or a **definition** (what a thing *is*, as opposed to what is
allowed). Items marked † were proposed as configurable numbers or patterns in the first version of this file
and are invariants since the owner decided that a rule is a boolean (section 3, "Not a switch").

| # | Invariant | Kind | Why it cannot be a setting |
|---|---|---|---|
| I1 | **Tenant isolation.** Every row belongs to one tenant; the tenant comes only from the credential; another tenant's record is indistinguishable from a missing one (`404`); references and uniqueness are per tenant. | security | A tenant that could loosen it would loosen it for the others. |
| I2 | **Authentication and key secrecy.** Every route needs a key; only its hash is stored; the plaintext is shown once and never through MCP; admin and tenant credentials are separate; a revoked key stays revoked and is never deleted; a key cannot revoke itself, so a tenant always keeps an active key. | security | A setting that weakens authentication is itself reachable only through authentication; self-revocation locks the tenant out for good. |
| I3 | **Attribution.** Ids, timestamps and actor fields are set by the server, never by the caller; every write, posting, confirmation and rule change records the key that did it. | integrity | "The agent is a user" only holds if every act is somebody's. |
| I4 | **Posted is final.** The ledger is append-only; a posted stock document and its number never change; a mistake is corrected by a reversing document, at most once per original, and a reversing document cannot be reversed; original and reversal sum to zero. | integrity | An editable ledger is not a ledger; every later number (balance, value, tax) rests on it. |
| I5 | **Only documents move stock, and the numbers agree.** Posting is atomic; stock on hand == sum of the ledger == stored balance, per article and warehouse, at every moment; a transfer changes no article's total; the ledger is in base units. | integrity | These are equalities, not policies; with one of them off the system reports stock it cannot explain. |
| I6 | **References hold.** A referenced record must exist in the tenant; a record that something references cannot be deleted (`IN_USE`), only retired; an article's `type` and base unit, and a conversion a draft uses, cannot change under the documents that depend on them. | integrity | Otherwise a posted quantity loses its unit, or a document its article. |
| I7 | **Identity.** A master has a code, unique in its tenant and resource, compared case-insensitively; a document number is unique per tenant. | integrity | `by-code` and `by-number` must find one record. |
| I8 | **Definitions.** What each document type is and its states (a draft has no effect and reserves nothing; an order is fulfilled only while `confirmed`; `closed` means no more); a document has at least one line and a line moves more than zero (a count states zero or more, one statement per article); a transfer has two different warehouses; a `service` article has no stock; the base unit has factor 1 and a line's unit must be a unit of its article; a document linked to an order has that order's partner (none, when the order has none) and its lines the order lines' articles; received, outstanding, incoming, reserved and available quantities are computed, never set; † a converted quantity is rounded to 6 decimals and a line amount to 2, half away from zero. | definition | Switching one off does not relax a rule, it makes a word mean nothing: a "transfer" from a warehouse to itself, a "count" with two answers. Rounding is how a number is computed, not whether a write is allowed. |
| I9 | **The shape of the API contract.** JSON shapes and types; unknown properties and query parameters are refused; text is trimmed, empty optional text is `null`, single-line text has no control characters; † a code consists of letters, digits, `.`, `_` and `-`; † a country code is two upper-case letters; the error document, its codes and the order of checks; the list envelope and paging (`limit` 1–500, default 50); a replace carries every field; one MCP tool per operation with the same fields and errors. | contract | Separately built clients (CLI, web, agents) are written against it; a contract that differs per tenant is not one. A code appears in URLs (`by-code`); its alphabet is part of the address. |
| I10 | **Platform bounds.** Exact decimals, never floating point; storage precision — † a quantity, a unit price and a conversion factor have at most 6 decimals, an amount 2; quantities to 999999999.999999; factors to 999999.999999; line amounts to 9999999999.99; † field lengths — code 50, name 200 (API key name 100), description and note 2000, reference 100, tax id 50, address lines 200, postal code 20, city and region 100; † at most 200 lines on a document or an order; a request body of at most 1 MB; a search text of at most 100 characters; the admin key and the MCP origin allow-list (settings of the operator, not of a tenant). | security / storage | They protect the shared service and the shared schema; one tenant cannot be given a wider column. They are bounds, not decisions: no business decides to have "at most 200 characters in a name". |
| I11 | **Structure the owner decided** (ADR-0019): every tenant has exactly one default warehouse and it is active (so it cannot be deactivated or deleted while default); every document has a warehouse in its header. | owner decision | Listed here because it is the owner's own rule; he can reopen it. *Whether the caller must name the warehouse* is configurable. |
| I12 | **The rule mechanism itself** (ADR-0020): a rule's value is `true` or `false`; a change is attributed and kept in the history; a change applies to new writes and rewrites nothing. | integrity | A rule about rules has to stand still. |

Not rules at all, so in neither class: **initial data** a tenant can change afterwards (the first key is
named `initial`; the first warehouse is `CENTRAL` / "Central warehouse") and **what nothing forbids** (a
master can always be renamed and deactivated; an order can be closed whatever was received; a tax id may
have any form).

## 3. Catalogue of configurable rules

Every rule is a switch: **key**, **name** (the statement — `true` means it holds), **default** (today's
behaviour) and what yes and no do. `<m>` stands for each of `uom`, `article`, `partner`, `warehouse`.

### Batch 012 — stock and order policies (spec 012, written)

| Key | Name | Default | Yes / no |
|---|---|---|---|
| `stock.negativeStockAllowed` | Stock may go below zero | `false` | No: a posting or reversal that would take stock on hand below zero is refused (`INSUFFICIENT_STOCK`). Yes: it is posted; stock, balance and available quantity may be negative. |
| `purchase.overReceiptAllowed` | More than ordered may be received on a purchase order | `false` | No: a receipt that takes an order line above its ordered quantity is refused (`QUANTITY_EXCEEDS_ORDER`). Yes: it is posted, without limit. |
| `sales.overDeliveryAllowed` | More than ordered may be delivered on a sales order | `false` | The same for deliveries. |
| `sales.reservedStockProtected` | Reserved stock is protected | `false` | No: reservation informs only. Yes: an issue, transfer or reversal that would take goods confirmed sales orders still await is refused (`STOCK_RESERVED`). |
| `purchase.partnerRequired` | Partner (supplier) required on a purchase order | `true` | Yes: an order saved without a supplier is refused. No: it may be saved, confirmed and received against without one. |
| `sales.partnerRequired` | Partner (customer) required on a sales order | `true` | The same for the customer of a sales order. |

### Batch 013 — master data (roadmap)

| Key | Name | Default | Yes / no |
|---|---|---|---|
| `uom.codeChangeable`, `article.codeChangeable`, `partner.codeChangeable`, `warehouse.codeChangeable` | The code of a (unit / article / partner / warehouse) may be changed | `true` | No: a replace that changes the code is refused. |
| `uom.nameUnique`, `article.nameUnique`, `partner.nameUnique`, `warehouse.nameUnique` | Names of (units / articles / partners / warehouses) must be unique | `false` | Yes: a second record with the same name is refused. |
| `article.descriptionRequired` | Description required on an article | `false` | — |
| `partner.roleRequired` | A partner must be a customer or a supplier | `true` | No: a partner with neither role may be saved. |
| `partner.taxIdRequired` | Tax id required on a partner | `false` | — |
| `partner.taxIdUnique` | Tax ids of partners must be unique | `false` | — |
| `partner.<f>Required`, `warehouse.<f>Required` for `<f>` = `addressLine1`, `addressLine2`, `postalCode`, `city`, `region`, `countryCode` (12 keys, e.g. `partner.cityRequired`) | (Field) required on a partner / on a warehouse | `false` | — |

### Batch 014 — documents (roadmap; may be split in two when written)

| Key | Name | Default | Yes / no |
|---|---|---|---|
| `references.inactiveAssignable` | An inactive record may be newly referenced | `false` | No: `REFERENCE_INACTIVE` on save. |
| `posting.activeMastersRequired` | Posting and confirmation need active warehouse, articles and partner | `true` | — |
| `stock.reversalActiveMastersRequired` | A reversal needs active warehouse, articles and partner | `false` | The default keeps mistakes correctable. |
| `document.pastDateAllowed` | A document or order may be dated before today | `true` | No: back-dating is refused. ("Today" needs a clock: spec 014 defines it.) |
| `document.futureDateAllowed` | A document or order may be dated after today | `true` | — |
| `stock.reversalBeforeOriginalAllowed` | A reversal may be dated before its original | `false` | — |
| `order.dueDateBeforeOrderDateAllowed` | The expected / requested date may be before the order date | `false` | — |
| `purchase.expectedDateRequired` | Expected date required on a purchase order | `false` | — |
| `sales.requestedDateRequired` | Requested date required on a sales order | `false` | — |
| `document.referenceRequired` | Reference required on a document or order | `false` | — |
| `document.repeatedArticleAllowed` | The same article may be on several lines | `true` | A count: never (I8). |
| `document.warehouseRequired` | The warehouse must be named on every new document | `false` | No: an omitted warehouse means the default warehouse. Yes: it is an error. |
| `stock.receiptPartnerRequired` | Partner required on a receipt that is not linked to an order | `false` | — |
| `stock.issuePartnerRequired` | Partner required on an issue that is not linked to an order | `false` | — |
| `purchase.supplierRoleRequired` | The partner of a purchase order must be a supplier | `true` | No: any partner (`PARTNER_ROLE_MISSING` is not answered). |
| `sales.customerRoleRequired` | The partner of a sales order must be a customer | `true` | — |
| `stock.partnerRoleRequired` | The partner of a receipt must be a supplier, of an issue a customer | `true` | — |
| `purchase.receiptOtherWarehouseAllowed` | A receipt may use a warehouse other than its order's | `false` | — |
| `sales.deliveryOtherWarehouseAllowed` | A delivery may use a warehouse other than its order's | `false` | — |
| `stock.outdatedCountAllowed` | A count whose book quantity is no longer current may be posted | `false` | No: `COUNT_OUTDATED`. Yes: posted against the stock at posting. |
| `price.zeroAllowed` | A unit price of zero is accepted | `true` | — |
| `order.reopenAllowed` | A closed order may be reopened | `true` | — |
| `order.closesWhenFulfilled` | An order closes itself when every line is fulfilled | `false` | — |
| `sales.confirmBeyondAvailableAllowed` | A sales order may be confirmed for more than is available | `true` | — |

### Not a switch — what the first version listed as configurable and why it no longer is

The owner's decision that a rule is a boolean leaves two honest places for a rule that was a number, a
pattern or a choice: the yes/no question behind it (the tables above), or the invariants. Moved to the
invariants:

| Was | Now | Why it is a bound and not a decision |
|---|---|---|
| `quantity.decimals` (0–6) | I10: six decimals | Six is the precision of storage. "Whole quantities only" is true of pieces and false of kilograms in the same company, so it is a property of a unit of measure, not of a tenant; it comes, if wanted, as a feature of units (ADR-0014 deferred it). |
| `document.maxLines` (1–1000) | I10: 200 lines | It protects the service (request size, time under the posting lock). No business decides to have short documents; a tenant that needs more splits the document. |
| `purchase.overReceiptPercent`, `sales.overDeliveryPercent` | switches `…Allowed` | The question is "may we receive more than ordered"; *yes* is without limit. |
| `<m>.code.maxLength`, `<m>.name.maxLength`, `apiKey.name.maxLength`, `article.description.maxLength`, `partner.taxId.maxLength`, `address.*.maxLength`, `document.reference.maxLength`, `document.note.maxLength` | I10: column lengths | A maximum length is the size of a column. |
| `<m>.code.pattern`, `address.countryCode.pattern` | I9: fixed forms | A code is part of a URL; a country code is the two-letter form. Tenant-written patterns are ruled out. |
| `partner.taxId.pattern` | nothing | No form is checked today and none is added (the core is jurisdiction-neutral). |
| `conversion.factor.decimals`, `price.decimals`, `amount.decimals` | I10: 6, 6 and 2 decimals | Storage precision. |
| `quantity.rounding`, `amount.rounding` | I8: half away from zero | A way of computing, with four possible values and no yes/no form; it refuses nothing. |
| `document.date.maxDaysPast`, `.maxDaysFuture` | switches `document.pastDateAllowed`, `document.futureDateAllowed` | The question is "may documents be back-dated / post-dated". |
| `stock.count.outdated` (`refuse` / `adjust`) | switch `stock.outdatedCountAllowed` | Two outcomes: a switch. |

### With other specs

| What | Spec | Note |
|---|---|---|
| Document numbers: prefix, digits, yearly restart, gapless or not, per document type | 017 Number series | Today `SR-`, `SI-`, `ST-`, `SC-`, `PO-`, `SO-` + 6 digits, gapless. A prefix and a number of digits are not yes/no: they are **data of a series** (a record the tenant edits, like a master), not rules. The yes/no parts (gapless; restart each year) are switches on this registry. Uniqueness stays I7. |
| Who may do what: manage keys, post, confirm, change rules, verify and rebuild balances | 021 Permissions | Today: every key everything (owner's decision 3). Permissions are per key, not a value per tenant; that spec decides their form. |
| A confirmed order can be changed | feature (order amendment, "Later") | Today immutable; close and make a new one. |
| Part of a posted document can be reversed | feature (returns, "Later") | Today whole and final. |
| `service` articles on orders | feature (018 Invoices) | Today only `stock` articles. |
| A draft reserves stock or order quantity | feature ("Later") | Today a draft has no effect (I8 as long as that is so). |
| Quantities in whole numbers only, per unit of measure | feature (units, ADR-0014) | Today every unit takes six decimals. |
| One tenant currency with other precision; several addresses per partner | feature ("Later") | — |

## 4. Inventory — every rule of specs 001–011a

One row per rule or validation. "Key / invariant" names the rule of section 3 or the invariant of section 2.

Rules that were classified configurable as a number, a length, a pattern or a rounding mode in the first
version are invariants here (owner's decision 2; section 3, "Not a switch").

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
| 003/R1 | API key name 1–100, need not be unique | INVARIANT | I10, I9 | 100 is the column; a name is required by the contract. |
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
| 001/R2, 002/R1, 004/R1 | Code: letters, digits, `.` `_` `-` | INVARIANT | I9 | A code is part of a URL (`by-code`); a pattern is not a yes/no rule. |
| same | Code: 1–50 characters | INVARIANT | I10 | 50 is the column. |
| 001/E1 | Code required | INVARIANT | I7 | Identity. |
| 001/R3, 002/R1, 004/R1 | Code unique per tenant and resource, case-insensitive; four namespaces | INVARIANT | I7 | Identity; enforced by an index shared by all tenants. |
| 001/R6, 002/R12, 004/R8 | A replace may change the code | CONFIGURABLE | `<m>.codeChangeable` (013) | A policy. |
| 001/R4, 002/R2, 004/R2 | Name 1–200 | INVARIANT | I10, I9 | 200 is the column; required because summaries carry `name`. |
| 001, 004 (implicit) | Names need not be unique | CONFIGURABLE | `<m>.nameUnique` (013) | A policy. |
| 001/R2 (tenant code, name) | Tenant code and name format | INVARIANT | I9, I10 | Set by the operator with the admin key; no tenant exists yet to configure it. |
| 001/R5, 002/R10, 004/R6, R14 | `isActive` defaults to `true`, role flags to `false`, on create | INVARIANT | I9 | Defaults of the contract. |
| 002/R3 | Description optional | CONFIGURABLE | `article.descriptionRequired` (013) | A required field. (≤ 2000: I10.) |
| 002/R4, R7 | `type` is `stock` or `service`; base unit required for both | INVARIANT | I8, I6 | Definition; a quantity needs a unit. |
| 002/R6, 004, 005/R3, R5, ADR-0008 | A reference must exist in the tenant | INVARIANT | I6, I1 | Integrity. |
| 002/R9, 005/R3, R5, 007/R4, R12, 011a/R5 | An inactive record cannot be newly referenced; a kept reference stays valid | CONFIGURABLE | `references.inactiveAssignable` (014) | A policy about retired records. |
| 002/R16, 005/R25, 007/R8, R10, 009/R37, 011a/R15 | A referenced record cannot be deleted (`IN_USE`) | INVARIANT | I6 | Integrity. |
| 005/R26, 007/R9, 009/R38 | `type` and base unit frozen once used or once conversions exist | INVARIANT | I6 | Posted quantities would change meaning. |
| 004/R15 | A partner is a customer or a supplier (at least one) | CONFIGURABLE | `partner.roleRequired` (013) | A policy (ADR-0011 default). |
| 004/R16 | Tax id not unique | CONFIGURABLE | `partner.taxIdUnique` (013) | A policy. |
| 004/R3 | Tax id optional | CONFIGURABLE | `partner.taxIdRequired` (013) | A required field. (≤ 50: I10. No form is checked: nothing forbids.) |
| 004/R3, R5 | Address fields optional, independent | CONFIGURABLE | `partner.<f>Required`, `warehouse.<f>Required` (013) | Required fields. (≤ 200 / 20 / 100: I10.) |
| 004/R4 | Country code two upper-case letters, not checked against a list | INVARIANT | I9 | The form of the field; a pattern is not a yes/no rule. |
| 004/R9 | Booleans are JSON booleans | INVARIANT | I9 | Contract. |
| 007/R2 | Factor > 0 | INVARIANT | I8 | Definition of a conversion. |
| 007/R2 | Factor ≤ 6 decimals | INVARIANT | I10 | Storage precision. |
| 007/R5, R1 | No conversion for the base unit; base unit has factor 1 | INVARIANT | I8 | Definition. |
| 007/R7 | A factor can change at any time | — | nothing forbids | Posted lines keep their own (I4). |
| 011/R2, R4–R6 | Exactly one active default warehouse; it cannot be deactivated or deleted; set-default needs an active target | INVARIANT | I11 | Owner's structure (ADR-0019). |
| 011/R3, R7 | The default is otherwise ordinary; a former default can be retired | — | nothing forbids | — |

### Stock documents (005–008, 011, 011a)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 005/R1, 006/R1, 008/R1 | Types `receipt`, `issue`, `transfer`, `count`; the type never changes | INVARIANT | I8 | Definition. |
| 005/R2 | `documentDate` required, a real `YYYY-MM-DD` date | INVARIANT | I9 | Contract. |
| 005/R2 | Any date, past or future | CONFIGURABLE | `document.pastDateAllowed`, `document.futureDateAllowed` (014) | A date policy; default: both allowed. |
| arch §5, 011/R8 | Every document has a warehouse | INVARIANT | I11 | Owner's structure. |
| 011/R8–R10 | Omitted `warehouseId` on create = the default (linked: the order's) | CONFIGURABLE | `document.warehouseRequired` (014) | A convenience a tenant may want off. |
| 005/R4 | At least one line | INVARIANT | I8 | A document without lines states nothing. |
| 005/R4, 008/R2, 009/R5 | At most 200 lines | INVARIANT | I10 | Protects the service; a bound, not a business decision (section 3, "Not a switch"). |
| 005/R4 | The same article on several lines | CONFIGURABLE | `document.repeatedArticleAllowed` (014) | A policy. |
| 008/R3 | A count names each article once | INVARIANT | I8 | Two counted quantities for one article contradict. |
| 005/R6 | Quantity > 0 (count: ≥ 0, 008/R4) | INVARIANT | I8 | A line moves something; direction comes from the type. |
| 005/R6 | Quantity ≤ 6 decimals | INVARIANT | I10 | Storage precision; whole-number quantities are a property of a unit, not of a tenant (section 3, "Not a switch"). |
| 005/R6 | Quantity ≤ 999999999.999999, a JSON number | INVARIANT | I10, I9 | Storage; contract. |
| 005/R7 | `reference` and `note` optional | CONFIGURABLE | `document.referenceRequired` (014) | A required field. (≤ 100 / ≤ 2000: I10.) |
| 005/R9–R11 | Draft: no effect, replaceable, deletable; posted: immutable | INVARIANT | I8, I4 | Definition; posted is final. |
| 005/R12, R14, 006/R6, R8 | Posting is atomic; entries and signs by type; a transfer sums to zero | INVARIANT | I5 | Integrity. |
| 005/R13, 006/R5, 009/R12, 011a/R12 | Posting and confirmation need active warehouse, articles (and, unlinked, partner) | CONFIGURABLE | `posting.activeMastersRequired` (014) | A policy about retired records. |
| 005/R15–R16, 006/R7, 010/R9 | **No negative stock** on issue, transfer, delivery | CONFIGURABLE | `stock.negativeStockAllowed` (**012**) | A policy (ADR-0012 default); owner's decision 5. |
| 006/R16, 008/R18 | No negative stock by reversal | CONFIGURABLE | `stock.negativeStockAllowed` (**012**) | The same rule. |
| 005/R18, 008/R13 | A receipt and a count are never refused for stock | — | nothing forbids | — |
| 005/R17, 006/R9, 008/R15, 009/R13 | Number = fixed prefix + 6 digits, gapless, per type, assigned at posting / confirmation | CONFIGURABLE | 017 Number series | Format and gaps are policy; uniqueness is I7. |
| 005/R19, 011/R19–R23 | Stock on hand = sum of the ledger = stored balance | INVARIANT | I5 | Equality. |
| 011/R24–R26 | Verify reports, rebuild repairs, neither changes the ledger | INVARIANT | I5 | They are how I5 is checked. |
| 005/R5, 009/R6 | Only `stock` articles on stock documents | INVARIANT | I8 | A service has no stock. |
| 006/R2 | A transfer's warehouses differ | INVARIANT | I8 | Definition. |
| 006/R3, 008/R1, 011a/R1 | `toWarehouseId` only on transfers; `partnerId` only on receipts and issues | INVARIANT | I8, I9 | Shape of each type. |
| 006/R11, R18–R19 | Only a posted, non-reversing document is reversed, once | INVARIANT | I4 | Net zero would break. |
| 006/R13 | Reversal is of the whole document | CONFIGURABLE | feature (returns) | A choice (ADR-0013); partial reversal is not built. |
| 006/R12 | Reversal date not before the original's | CONFIGURABLE | `stock.reversalBeforeOriginalAllowed` (014) | A date policy. |
| 006/R17, 011a/R14 | Reversal does not need active masters | CONFIGURABLE | `stock.reversalActiveMastersRequired` (014) | A policy; default keeps mistakes correctable. |
| 007/R12 | A line's unit is a unit of its article | INVARIANT | I8 | Without a factor there is no base quantity. |
| 007/R14 | Base quantity rounded to 6 decimals, half away from zero | INVARIANT | I8, I10 | How a number is computed; it refuses nothing. |
| 007/R15 | A base quantity of 0 or above the maximum is refused | INVARIANT | I8, I10 | Moves nothing / cannot be stored. |
| 007/R17, R20 | A draft follows the current factor; a posted line keeps its own | INVARIANT | I8, I4 | Draft fixes nothing; posted is final. |
| 007/R19 | The ledger is in base units | INVARIANT | I5 | Sums need one unit. |
| 008/R6–R7 | Book quantity taken at save; difference = counted − book | INVARIANT | I8 | Definition of a count. |
| 008/R10 | An outdated count is refused (`COUNT_OUTDATED`) | CONFIGURABLE | `stock.outdatedCountAllowed` (014) | A policy (ADR-0015 default). |
| 011a/R2 | Partner on a receipt / issue is optional | CONFIGURABLE | `stock.receiptPartnerRequired`, `stock.issuePartnerRequired` (014) | A required field. |
| 011a/R6, R12 | Its role: supplier on a receipt, customer on an issue | CONFIGURABLE | `stock.partnerRoleRequired` (014) | A role requirement. |
| 011a/R8–R10 | A linked document has the order's partner (none, when the order has none: 012) | INVARIANT | I8 | It is what "linked" means. |

### Orders and fulfilment (009, 010)

| Source | Rule today | Class | Key / invariant | Reason |
|---|---|---|---|---|
| 009/R1, 010/R1 | **An order names a partner** (supplier / customer) | CONFIGURABLE | `purchase.partnerRequired`, `sales.partnerRequired` (**012**) | The owner's own example of a rule. At most one partner per order stays I8. |
| 009/R1, 010/R1 | An order has one warehouse | INVARIANT | I11 | Owner's structure. |
| 009/R2, 010/R1 | Supplier must be a supplier, customer a customer | CONFIGURABLE | `purchase.supplierRoleRequired`, `sales.customerRoleRequired` (014) | A role requirement. |
| 009/R3 | `orderDate` required | INVARIANT | I9 | Contract. |
| 009/R3, 010/R1 | `expectedDate` / `requestedDate` optional, not before `orderDate` | CONFIGURABLE | `purchase.expectedDateRequired`, `sales.requestedDateRequired`, `order.dueDateBeforeOrderDateAllowed` (014) | Required field; date policy. |
| 009/R6 | Only `stock` articles on orders | CONFIGURABLE | feature (018) | A gap, not a principle: services are ordered once they can be invoiced. |
| 009/R8 | Unit price ≥ 0 | INVARIANT | I8 | A negative price is a credit, another document. |
| 009/R8 | Unit price 0 allowed | CONFIGURABLE | `price.zeroAllowed` (014) | A policy. |
| 009/R8 | Unit price ≤ 6 decimals | INVARIANT | I10 | Storage precision. |
| 009/R9 | Line amount = quantity × price, 2 decimals, half away from zero | INVARIANT | I8, I10 | A formula and its precision; it refuses nothing. |
| 009/R9 | Line amount ≤ 9999999999.99 | INVARIANT | I10 | Storage. |
| 009/R11–R16 | States `draft -> confirmed <-> closed`; a draft orders nothing | INVARIANT | I8 | Definition. |
| 009/R14 | **A confirmed order is immutable** | CONFIGURABLE | feature (order amendment) | A choice (ADR-0016), not integrity: an order writes no ledger. |
| 009/R16 | A closed order can be reopened | CONFIGURABLE | `order.reopenAllowed` (014) | A policy. |
| ADR-0016 | Orders never close by themselves | CONFIGURABLE | `order.closesWhenFulfilled` (014) | A policy. |
| 009/R18–R20, 010/R5–R6 | Only a receipt links to a purchase order, only an issue to a sales order; fixed at creation; every line names an order line | INVARIANT | I8 | What a link is. |
| 009/R21.2, R24, R31 | Fulfilment only against a `confirmed` order (`ORDER_NOT_OPEN`) | INVARIANT | I8 | What `draft` and `closed` mean; the tenant reopens. |
| 009/R21.4 | Receipt / delivery only in the order's warehouse | CONFIGURABLE | `purchase.receiptOtherWarehouseAllowed`, `sales.deliveryOtherWarehouseAllowed` (014) | A policy (ADR-0016 default). |
| 009/R21.4 | A line's article is its order line's | INVARIANT | I8 | Another article is another order line. |
| 009/R25, R27, R29, 010/R8 | Received / delivered, outstanding and status are computed from posted documents | INVARIANT | I8, I5 | Derived numbers. |
| 009/R26 | **Never receive more than ordered** | CONFIGURABLE | `purchase.overReceiptAllowed` (**012**) | A policy (ADR-0016 default). |
| 010/R7 | **Never deliver more than ordered** | CONFIGURABLE | `sales.overDeliveryAllowed` (**012**) | The same. |
| 009/R34, 010/R14 | A linked document is reversed whatever the order's status | — | nothing forbids | — |
| 010/R4 | Confirmation never refused for stock | CONFIGURABLE | `sales.confirmBeyondAvailableAllowed` (014) | A policy. |
| 010/R15–R16, 009/R35 | Reserved, incoming, available: definitions | INVARIANT | I8 | Derived numbers. |
| 010/R17 | **Reservation informs, does not block** | CONFIGURABLE | `sales.reservedStockProtected` (**012**) | A policy (ADR-0017 default). |


## 5. Owner's decisions

**Decided by the owner, 2026-10-10** (recorded in ADR-0020, "Owner's decisions"):

1. **The invariants I1–I12 are approved. Only business logic is configurable.**
2. **A rule is a boolean** — a named yes/no switch in plain language ("Partner required on an order: True or
   False"); not a number, a percentage or a multi-valued choice. This also settles what "however they want"
   covers: the switches we define — no numeric parameters, no rules a tenant writes itself.
3. **Every tenant key, agents included, may change rules.** An agent refused by a rule is told its key and can
   switch it; the history shows it.
4. **Order of work**: 012, then the retrofit 013 and 014, then valuation.
5. **Allowing negative stock is a business rule each tenant decides for itself**, accepted with its
   consequences: the database no longer checks the balance, and valuation will have to say what stock below
   zero costs.

**What follows from decision 2 — for the owner to see, not to decide again:**

- Over-receipt and over-delivery are *allowed: yes/no*. Yes means without limit; "at most 10 % more" cannot
  be set.
- Numbers and forms that the first version offered as settings are fixed again: decimals of quantities,
  prices and factors, lengths of codes and texts, the characters of a code, the country-code form, rounding,
  200 lines per document (section 3, "Not a switch"; marked † in section 2). They are bounds of the platform,
  not business logic.
- "Partner required on an order" exists as two switches, one per kind of order (`purchase.partnerRequired`,
  `sales.partnerRequired`), both *yes* by default, in spec 012. "Partner required on a receipt / an issue"
  (default *no*) follows in batch 014.

**Architect's defaults that still stand** (not among the owner's answers; each holds until he says otherwise):

- **Tightening is not retroactive, and a replace is judged whole.** Old records stay valid; but the next
  `PUT` of an old record must satisfy the current rules. A draft order saved without a partner while that
  was allowed can still be confirmed after the partner became required.
- **Rules whose other value is a feature** (a confirmed order can be changed; partial reversal; services on
  orders; drafts that reserve) stay fixed until that feature is specified. They are not invariants.
- **`sales.reservedStockProtected`** protects reserved stock from every other movement, but a stock count is
  never blocked (it states what is physically there).
- **Our defaults never change under an existing tenant**: if we change one, existing tenants keep the old
  value as their own.
