# ADR-0020: Configurable business rules

Status: accepted (2026-10-10) — owner requirement; **amended the same day by the owner's decisions** (section
"Owner's decisions" below): a rule is a **boolean switch**. The first text of this ADR had six value types
(`boolean`, `choice`, `integer`, `decimal`, `text`, `pattern`); that is withdrawn and recorded under
"Alternatives".

## Context
Owner, 2026-10-10: *every validation and business rule must be configurable; we offer default business rules
and people can change them however they want; enforced from now on, from the start.* `CLAUDE.md` carries it as
a project rule.

As built (specs 001–011a) every rule is a literal in a check: stock never goes negative, a receipt never
exceeds its order, a reservation never blocks, an order always has a partner. The earlier ADRs recorded most
of them as "architect's default until the owner says otherwise" — the owner has now said: the default stays,
the tenant decides.

Two things have to be settled at once: **the mechanism** (this ADR) and **which rules it covers** — the
inventory of every rule of specs 001–011a, each classified configurable or invariant, is `docs/rules.md`.

## Owner's decisions (2026-10-10)
Answers to the questions the first text of this ADR left for him (`docs/rules.md` §5):

1. **The invariants I1–I12 are approved. Only business logic is configurable.**
2. **A rule is a boolean.** His example: *"Partner required on an order: True or False."* A rule is a named
   yes/no switch in plain language — not a number, not a percentage, not a choice among several values.
3. **Every tenant key, agents included, may change rules.**
4. **Order of work**: spec 012, then the retrofit (013, 014), then valuation.
5. **Allowing negative stock is a business rule each tenant decides for itself**, accepted with its
   consequences (no database check on the balance; valuation must say what stock below zero costs).

What decision 2 rules out, stated once so that no spec reopens it:
- **No numeric parameters.** No tolerance percentage, no number of decimals, no maximum length, no number of
  days. A rule that was a number is either the yes/no question behind it ("more than ordered may be
  received: yes/no" — *yes* means without limit) or, where the number was a bound of the platform and not a
  decision of a business, an invariant (I10: storage precision, column lengths, lines per document).
- **No multi-valued choices.** A rule that had more than two outcomes keeps two; a third outcome is a
  feature with its own switch, if it is ever built.
- **No free text and no patterns** as rule values (code alphabets, tax-id forms): what is fixed is an
  invariant of the contract (I9), what is not checked stays unchecked.
- **No tenant-written rules.** No conditions, no expressions. A rule exists only after a spec defines it.

## Decision

### 1. Every rule is a named switch with a default; what is not, is an invariant with a reason
A check that can refuse a write is one of two things:
- a **rule**: it has a key, a plain-language name and a default, and each tenant has its own value — `true`
  or `false`;
- an **invariant**: it cannot be switched off without breaking integrity, security or the contract, or it is
  a bound of the platform. It is listed in `docs/rules.md` with its reason. The list is approved by the
  owner.

There is no third kind. A business decision about whether a write is accepted may appear in the code in
exactly one place: as a rule definition whose value the check receives. A literal number in a check is a
platform bound (I10) and is listed as such.

### 2. The rule registry (code) and the catalogue (docs)
A rule is defined once, in `Xerp.Domain`, as a **rule definition**:

| Part | Meaning |
|---|---|
| `key` | Stable identifier, `area.statement`: two lowerCamel segments separated by a dot — `purchase.partnerRequired`, `stock.negativeStockAllowed`. Part of the contract like an error code: never renamed, never reused. |
| `group` | The first segment (`stock`, `purchase`, `sales`, `partner`, `document`, …), for listing. |
| `name` | The switch in plain language, written as a statement that is true or false: "Partner required on a purchase order", "Stock may go below zero". **`true` always means: the statement in the name holds.** |
| `description` | One paragraph, in English, written for an agent: which operations the rule affects, what happens when it is `true` and what when it is `false`. |
| `default` | `true` or `false`: the value we ship. A tenant that has set nothing has this value. |

There is no `type` and no `allowed`: every rule is a boolean. The statement is worded the way the owner of a
business would ask it, so the default is `true` for some rules ("partner required") and `false` for others
("stock may go below zero"); the name, not a convention about polarity, says what `true` does.

The registry is the list of all definitions. `docs/rules.md` is its human catalogue; `GET /rules` is its live
form; a test pins the literal list of keys with their defaults, as one pins the tool list (architecture §9,
inventory tests). The three never disagree: a spec that adds a rule adds it to all three.

### 3. Storage: values per tenant, defaults in code
- `RuleValue` (tenant-owned): `TenantId`, `Key`, `Value` (boolean), `UpdatedAt`, `UpdatedBy`. Primary key
  `(TenantId, Key)`. **Only what a tenant has set is stored**; no row means "the default". A new tenant has no
  rows and needs no seeding; a new rule needs no migration of data.
- `RuleChange` (tenant-owned, append-only): `Id`, `TenantId`, `Key`, `Action` (`set` | `reset`), `OldValue`,
  `NewValue` (booleans: the effective values before and after), `ChangedAt`, `ChangedBy`. One row per change,
  written in the transaction that changes the value.
- Both are ordinary tenant-owned tables: `TenantId`, query filter, tenant-inclusive foreign key to `ApiKeys`
  for the actor (architecture §3, §8). A rule value is tenant data like any other.

### 4. How a rule is read (Domain and Application)
- **Domain** holds the definitions and the checks. A check is a function of its inputs *and the rule's value*:
  it receives the boolean as an argument and never fetches it. Domain stays without dependencies.
- **Application** has one port, `IRules`, that answers "the value of this definition for the current tenant".
  An operation asks for the values it needs, passes them to the Domain check, and — when a rule refuses —
  builds the error that names it (decision 9).
- **Api and MCP** never read a rule. They are thin as before.
- An operation reads rules **once, at its own start**, and judges the whole request by that one set. An
  operation that runs under the per-tenant lock (ADR-0012 amendment: every posting, reversal, confirmation,
  save of a document) reads them **after it holds the lock**.

### 5. How a tenant reads and changes rules (HTTP and MCP)

| Operation | HTTP | MCP tool |
|---|---|---|
| List rules — every rule of the registry with name, description, default, current value and whether it is the tenant's own | `GET /api/v1/rules` | `rule_list` |
| Get one | `GET /api/v1/rules/{key}` | `rule_get` |
| Set | `PUT /api/v1/rules/{key}` with `{ "value": true \| false }` | `rule_set` |
| Reset to default | `POST /api/v1/rules/{key}/reset` | `rule_reset` |
| History of changes | `GET /api/v1/rule-changes` | `rule_change_list` |

A rule's representation carries its whole definition, so an agent can discover what is configurable and read
what yes and no mean without documentation. `source` is `"default"` or `"tenant"`. Exact shapes: spec 012.

Every tenant key, human or agent, may read and change rules (owner's decision 3). An agent that a rule
refused can switch that rule; the history shows that it did. The permissions spec (roadmap 021) can restrict
it later.

### 6. Validating a new value
- Unknown key -> `404 NOT_FOUND` (the key addresses the record).
- A value that is not the JSON literal `true` or `false` — a string, a number, `null`, an array, an object —
  -> `400 VALIDATION_FAILED` with key `value`. Nothing is stored.
- No rule's validity depends on another rule's current value. Where two rules meet, each spec says what
  the combination does; no combination is an error. (This keeps "set" and "reset" always possible, in any
  order.)
- Setting the current value again succeeds and changes nothing — no `RuleChange`, no new `updatedAt`.

### 7. Existing data: a rule judges new writes; nothing is rewritten
- A changed rule applies to every operation that **starts after the change is committed**. Nothing that is
  stored is re-checked, changed, flagged or deleted because a rule changed — not when it is tightened, not
  when it is loosened.
- A record that no longer satisfies a tightened rule stays valid: it is read, referenced, posted against and
  reversed as before.
- **A write is judged whole by the rules of its moment.** Create and replace (`PUT`) judge every field they
  carry; so the next replace of an old record must satisfy the current rules (or the tenant loosens the rule).
  Posting, confirmation and reversal apply the rules that belong to *them* (stock, order quantities,
  reservation) with the values at that moment; they do not re-validate the form of a draft saved earlier.
  Posted documents are immutable (ADR-0007) and are never judged again.
- **Rules about a quantity that is already "out of range" judge the movement, not the state.** After negative
  stock was allowed and is refused again, a pair may still be negative; a posting is then refused only if it
  *decreases* a pair to below zero. The same form is used wherever a rule guards a running total.
- **Our own defaults.** A release never changes the behaviour of an existing tenant by changing a default: if
  a shipped default must change, the migration first writes the old default as the tenant's own value for
  every existing tenant (a `RuleChange` with no actor). New tenants get the new default.

### 8. Attribution
- `RuleValue.UpdatedAt` / `UpdatedBy` and every `RuleChange` record the API key that changed the rule and
  when; the key says whether it was a human or an agent (ADR-0003). The rule's representation shows
  `updatedAt` / `updatedBy`; the history shows every change with the value before and after.
- The history is append-only and has no delete. When the audit log (roadmap) exists it will also record
  these writes; `GET /rule-changes` stays.

### 9. Errors name the rule that refused
The error `code` stays what it is (`INSUFFICIENT_STOCK`, `VALIDATION_FAILED`, …): codes are the contract and
do not depend on configuration. A refusal that a configurable rule caused adds one member to the problem
document and to the MCP tool error:

```json
{ "code": "INSUFFICIENT_STOCK", "status": 409, "…": "…",
  "errors": { "lines[0].quantity": ["…"] },
  "rules": [ { "key": "stock.negativeStockAllowed", "value": false, "fields": ["lines[0].quantity"] } ] }
```

`rules` lists every rule that refused, with the value it had and the `errors` keys it produced. It is absent
when no configurable rule refused — then the refusal is an invariant's. So a caller always learns one of two
things: *which switch forbids this*, or *that no switch does*.

### 10. Caching and the per-tenant lock
- **No cache across requests.** A tenant's values are one indexed read of a handful of rows, done once per
  operation. A later cache must be keyed by a per-tenant version that is read under the lock; until someone
  measures a need, there is none. (This also keeps several API instances correct.)
- **A change of a rule takes the same per-tenant lock as postings** (ADR-0012 amendment, condition 1: a write
  whose effect other writes' decisions depend on). Together with decision 4, every posting, reversal and
  confirmation is judged entirely by the rules before the change or entirely by the rules after it — never by
  a mixture — and the result of parallel postings and rule changes is that of some order of them.
- Reading rules (`GET /rules`) takes no lock.

## What it cannot do
"Change them however they want" is met as: **every rule we ship can be switched on or off, by the tenant, at
any time, over the same API an agent uses.** Honestly stated limits:

1. **A rule is yes or no.** A tenant that allows over-receipt allows it without limit; "at most 10 % more"
   cannot be configured. A tenant cannot choose fewer decimals, shorter codes or a date window in days.
   (Owner's decision 2.)
2. **A tenant cannot write a rule of its own.** No conditions, no expressions: "refuse issues above 100 pieces
   to partner X" cannot be configured. A rule exists only after a spec defines it. (Owner's decision 2.)
3. **Values are per tenant only** — not per warehouse, article, partner, document or API key. Where a
   distinction matters the registry defines separate keys (`purchase.partnerRequired` and
   `sales.partnerRequired`).
4. **Invariants and platform bounds** cannot be switched off (`docs/rules.md` §2): six decimals of storage,
   column lengths, 200 lines per document, the size limits that protect the shared service.
5. **Where the other behaviour does not exist yet, there is nothing to switch to.** "A confirmed order is
   immutable" is a business choice, not an invariant, but its alternative is a feature (order amendment). Such
   rules are marked in the inventory and become switches with the spec that builds the alternative.
6. No effective dates, no scheduled changes, no "what would this change refuse" preview, no warnings (the API
   has refusals only).

Limit 3 can be lifted later inside this design (a scope column next to the key) without changing the
endpoints, the storage or the way an operation reads a rule. Limits 1 and 2 are the owner's decision; lifting
them is a new decision, not a refinement.

## Alternatives
- **Boolean switches: a table of set values + a registry in code.** **Chosen** (the owner's form of the
  rule). One small table, one port, uniform listing, attribution and error naming for every rule; the checks
  stay ordinary, testable code; every rule can be read aloud as a question and answered yes or no, by a
  person or an agent; validation of a value is trivial and no combination of values is invalid. Costs limits
  1, 2 and 3.
- **Typed values** (`boolean`, `choice`, `integer`, `decimal`, `text`, `pattern`, each with allowed values) —
  the first text of this ADR. More expressive: a tolerance in percent, decimals per tenant, a tenant's own
  code pattern. **Withdrawn on the owner's decision 2**: a rule must be a question a business owner can
  answer yes or no; ranges, patterns and "which of four rounding modes" are settings for a technician, each
  needs validation of its own and bounds that are themselves invariants, and tenant-supplied patterns bring a
  security question (matching cost) for no business gain.
- **Rule engine / expression language** (tenant-authored conditions in CEL, JSON Logic or similar, evaluated at
  each write). The only alternative that meets "however they want" literally. Rejected: every check becomes
  interpreted tenant code that runs inside the posting lock; an expression needs data (stock, order progress,
  partner) and therefore a query model exposed to it; a refusal can no longer have a stable error code or be
  predicted by an agent from a list; a wrong expression can make a tenant unable to post at all; and the
  security surface (evaluation cost, data reach) is new. Ruled out by the owner's decision 2.
- **One column per rule** (on `Tenants` or a one-row settings table). Typed by the database and trivially
  read, but every new rule is a schema migration, there is no uniform list / set / reset / history, "the
  tenant's own value" and "our default" cannot be told apart, and a 100-column row is where it ends.
- **Deployment configuration** (`appsettings`, environment). Not per tenant; changing it needs the operator.
  Kept only for what belongs to the operator: the admin key, MCP allowed origins.
- **Seed every rule as a row at tenant creation.** Makes "the tenant's own value" and "our default"
  indistinguishable and turns every new rule into a data migration. Rejected: defaults live in code.
- **Re-validate stored data when a rule is tightened** (refuse the change, or flag violators). Refusing makes
  tightening impossible in any tenant with history; flagging needs a scan of everything per rule. Rejected:
  the rule applies to new writes (decision 7).
- **Unchanged fields are not re-judged on replace** (as ADR-0008 does for an inactive reference that is kept).
  Kinder to old records, but validation would need the stored record before it can answer `400`, reversing
  the order of checks (architecture §5), and every validator would grow a second path. Rejected for value
  rules; ADR-0008's exemption for kept references stays as it is.
- **A rule about who may change rules** (`human` keys only). Dropped: until permissions exist any key can
  create a human key, so it would guard against accident only while looking like a control; and the owner
  decided that every key may (decision 3). It is the permissions spec's.

## Consequences
- Statements the earlier documents made absolutely become "by default": stock is never negative (ADR-0012,
  ADR-0018), never more than ordered (ADR-0016), reservation informs (ADR-0017), an order has a partner
  (ADR-0016). Each of those ADRs gets a one-line amendment pointing here. What remains absolute is the
  invariant next to each: stored balance == sum of the ledger; progress is computed from posted documents.
- What the first text made configurable as a number, a length or a pattern is fixed again, as a platform
  bound or a contract shape: quantity, price and factor decimals, code and text lengths, code alphabet,
  country-code form, rounding, 200 lines per document (`docs/rules.md` §3, "Not a switch"). Fewer rules, each
  easier to understand; a tenant with a real need for one of them needs a new decision of the owner.
- The database stops being a second barrier for configurable rules: the check constraint
  `StockBalance.Quantity >= 0` (spec 011) goes with spec 012, and the partner column of an order becomes
  nullable. Constraints remain for invariants only.
- Every existing check that is classified configurable must move onto the registry. That is the retrofit
  (roadmap 012–014) with **no change of default behaviour**: with no value set, every test of specs 001–011a
  passes unchanged — that sentence is an acceptance criterion of each batch.
- Every later spec pays a small, fixed price per rule: a definition, a catalogue row, criteria for both
  values (architecture §11).
- The combinations multiply. Tests cover each rule at `true` and at `false`, and the combinations a spec
  names; not the full product. A defect that only a rare combination shows is possible.
- Loosened rules reach later features: valuation (roadmap) must define the cost of stock that is negative;
  invoices must cope with over-delivery and with an order that has no partner. Those specs say so when they
  come.
- An agent that is refused is told which rule refused it and can switch that rule. That is the vision
  (the agent is a user) and an accepted risk (owner's decision 3); the history shows who did it.
- Error documents gain `rules`. It is an added member: clients that branch on `code` are unaffected.
