# ADR-0020: Configurable business rules

Status: accepted (2026-10-10) — owner requirement. Decisions 1 and 9 are the owner's words; the rest are the
architect's and are listed for the owner in `docs/rules.md` §5.

## Context
Owner, 2026-10-10: *every validation and business rule must be configurable; we offer default business rules
and people can change them however they want; enforced from now on, from the start.* `CLAUDE.md` carries it as
a project rule.

As built (specs 001–011a) every rule is a literal in a check: stock never goes negative, a receipt never
exceeds its order, a reservation never blocks, a quantity has six decimals, a document has 200 lines, a code
matches one pattern. The earlier ADRs recorded most of them as "architect's default until the owner says
otherwise" — the owner has now said: the default stays, the tenant decides.

Two things have to be settled at once: **the mechanism** (this ADR) and **which rules it covers** — the
inventory of every rule of specs 001–011a, each classified configurable or invariant, is `docs/rules.md`.

## Decision

### 1. Every rule is a named rule with a default; what is not, is an invariant with a reason
A check that can refuse a write is one of two things:
- a **rule**: it has a key, a type, a default and allowed values, and each tenant has its own value;
- an **invariant**: it cannot be switched off without breaking integrity, security or the contract. It is
  listed in `docs/rules.md` with its reason. The list is short and the owner approves it.

There is no third kind. A literal that decides whether a write is accepted may appear in the code in exactly
one place: as the `default` of a rule definition, or as the bound of its allowed values.

### 2. The rule registry (code) and the catalogue (docs)
A rule is defined once, in `Xerp.Domain`, as a **rule definition**:

| Part | Meaning |
|---|---|
| `key` | Stable identifier, `area.subject[.aspect]`, lowerCamel segments separated by dots: `stock.negativeStock`, `purchase.overReceiptPercent`. Part of the contract like an error code: never renamed, never reused. |
| `group` | The first segment (`stock`, `purchase`, `sales`, `quantity`, `document`, …), for listing. |
| `name`, `description` | One line and one paragraph, in English, written for an agent: what the rule decides, which operations it affects, what each value means. |
| `type` | One of the six types below. |
| `default` | The value we ship. A tenant that has set nothing has this value. |
| `allowed` | What a tenant may set: per type, below. Its ends are **platform bounds** (storage precision, column length, protection of the shared service) and are invariants. |

Types:

| `type` | JSON value | `allowed` |
|---|---|---|
| `boolean` | `true` / `false` | — |
| `choice` | string | `values`: the list; every value is documented in the description |
| `integer` | number without fraction | `min`, `max`; `nullable: true` where "no limit" (`null`) is a value |
| `decimal` | number | `min`, `max`, `decimals`; `nullable` as above |
| `text` | single-line string | `maxLength` |
| `pattern` | string holding a regular expression | `maxLength` (200). It must compile for linear-time (non-backtracking) matching — no look-around, no back-references — and is matched against the whole value. A tenant's pattern can therefore never hang a request. |

The registry is the list of all definitions. `docs/rules.md` is its human catalogue; `GET /rules` is its live
form; a test pins the literal list of keys with their defaults, as one pins the tool list (architecture §9,
inventory tests). The three never disagree: a spec that adds a rule adds it to all three.

### 3. Storage: values per tenant, defaults in code
- `RuleValue` (tenant-owned): `TenantId`, `Key`, `Value` (JSON), `UpdatedAt`, `UpdatedBy`. Primary key
  `(TenantId, Key)`. **Only what a tenant has set is stored**; no row means "the default". A new tenant has no
  rows and needs no seeding; a new rule needs no migration of data.
- `RuleChange` (tenant-owned, append-only): `Id`, `TenantId`, `Key`, `Action` (`set` | `reset`), `OldValue`,
  `NewValue` (the effective values before and after), `ChangedAt`, `ChangedBy`. One row per change, written in
  the transaction that changes the value.
- Both are ordinary tenant-owned tables: `TenantId`, query filter, tenant-inclusive foreign key to `ApiKeys`
  for the actor (architecture §3, §8). A rule value is tenant data like any other.

### 4. How a rule is read (Domain and Application)
- **Domain** holds the definitions and the checks. A check is a function of its inputs *and the rule's value*:
  it receives the value as an argument and never fetches it. Domain stays without dependencies.
- **Application** has one port, `IRules`, that answers "the value of this definition for the current tenant"
  (typed by the definition). An operation asks for the values it needs, passes them to the Domain check, and —
  when a rule refuses — builds the error that names it (decision 8).
- **Api and MCP** never read a rule. They are thin as before.
- An operation reads rules **once, at its own start**, and judges the whole request by that one set. An
  operation that runs under the per-tenant lock (ADR-0012 amendment: every posting, reversal, confirmation,
  save of a document) reads them **after it holds the lock**.

### 5. How a tenant reads and changes rules (HTTP and MCP)

| Operation | HTTP | MCP tool |
|---|---|---|
| List rules — every rule of the registry with definition, default, current value and whether it is the tenant's own | `GET /api/v1/rules` | `rule_list` |
| Get one | `GET /api/v1/rules/{key}` | `rule_get` |
| Set | `PUT /api/v1/rules/{key}` with `{ "value": … }` | `rule_set` |
| Reset to default | `POST /api/v1/rules/{key}/reset` | `rule_reset` |
| History of changes | `GET /api/v1/rule-changes` | `rule_change_list` |

A rule's representation carries its whole definition, so an agent can discover what is configurable, read what
each value means and learn the allowed values without documentation. `source` is `"default"` or `"tenant"`.
Exact shapes: spec 012.

Every tenant key may read and change rules, as it may do everything else in its tenant until the permissions
spec (ADR-0003). *(default — owner decision 3 in `docs/rules.md` §5: an agent can change a rule that refused
it.)*

### 6. Validating a new value
- Unknown key -> `404 NOT_FOUND` (the key addresses the record).
- A value of the wrong JSON type or outside `allowed` -> `400 VALIDATION_FAILED` with key `value`; the
  `detail` states the allowed values. Nothing is stored.
- No rule's allowed values depend on another rule's current value. Where two rules meet, each spec says what
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
  "rules": [ { "key": "stock.negativeStock", "value": "refuse", "fields": ["lines[0].quantity"] } ] }
```

`rules` lists every rule that refused, with the value it had and the `errors` keys it produced. It is absent
when no configurable rule refused — then the refusal is an invariant's. So a caller always learns one of two
things: *which setting forbids this*, or *that no setting does*.

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
"Change them however they want" is met as: **every rule we ship can be set, by the tenant, to any value in its
documented range, at any time, over the same API an agent uses.** Honestly stated limits:

1. **A tenant cannot write a rule of its own.** No conditions, no expressions: "refuse issues above 100 pieces
   to partner X" cannot be configured. A rule exists only after a spec defines it.
2. **Values are per tenant only** — not per warehouse, article, partner, document or API key. Where a
   distinction matters the registry defines separate keys (`purchase.…` and `sales.…`).
3. **Ranges end at platform bounds**: six decimals of storage, column lengths, the size limits that protect
   the shared service.
4. **Invariants** cannot be switched off (`docs/rules.md` §2).
5. **Where the other behaviour does not exist yet, there is nothing to switch to.** "A confirmed order is
   immutable" is a business choice, not an invariant, but its alternative is a feature (order amendment). Such
   rules are marked in the inventory and become rules with the spec that builds the alternative.
6. No effective dates, no scheduled changes, no "what would this change refuse" preview, no warnings (the API
   has refusals only).

Limits 1 and 2 are the ones a real tenant will meet. Both can be lifted later *inside this design* — a scope
column next to the key, an `expression` type next to `pattern` — without changing the endpoints, the storage
or the way an operation reads a rule.

## Alternatives
- **Settings table with typed keys + registry in code.** **Chosen.** One small table, one port, uniform
  listing, attribution and error naming for every rule; the checks stay ordinary, testable code. Costs limits
  1 and 2.
- **Rule engine / expression language** (tenant-authored conditions in CEL, JSON Logic or similar, evaluated at
  each write). The only alternative that meets "however they want" literally. Rejected for now: every check
  becomes interpreted tenant code that runs inside the posting lock; an expression needs data (stock, order
  progress, partner) and therefore a query model exposed to it; a refusal can no longer have a stable error
  code or be predicted by an agent from a list; a wrong expression can make a tenant unable to post at all; and
  the security surface (evaluation cost, data reach) is new. It would be built *on top of* this registry, not
  instead of it, when a tenant asks for something values cannot express.
- **One column per rule** (on `Tenants` or a one-row settings table). Typed by the database and trivially
  read, but every new rule is a schema migration, there is no uniform list / set / reset / history, tenants
  have no tenant-side update operation today, and a 100-column row is where it ends.
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
- **A rule about who may change rules** (`human` keys only). Considered for spec 012 and dropped: until
  permissions exist any key can create a human key, so it would guard against accident only while looking
  like a control. It is the permissions spec's.

## Consequences
- Statements the earlier documents made absolutely become "by default": stock is never negative (ADR-0012,
  ADR-0018), never more than ordered (ADR-0016), reservation informs (ADR-0017), six decimals, 200 lines. Each
  of those ADRs gets a one-line amendment pointing here. What remains absolute is the invariant next to each:
  stored balance == sum of the ledger; progress is computed from posted documents.
- The database stops being a second barrier for configurable rules: the check constraint
  `StockBalance.Quantity >= 0` (spec 011) goes with spec 012. Constraints remain for invariants only.
- Every existing check that is classified configurable must move onto the registry. That is several specs of
  retrofit (roadmap 012–014 and the number-series spec) with **no change of default behaviour**: with no
  value set, every test of specs 001–011a passes unchanged — that sentence is an acceptance criterion of each
  batch.
- Every later spec pays a small, fixed price per rule: a definition, a catalogue row, criteria for at least
  two values (architecture §11).
- The combinations multiply. Tests cover each rule at its default and at one other value, and the
  combinations a spec names; not the full product. A defect that only a rare combination shows is possible.
- Loosened rules reach later features: valuation (roadmap) must define the cost of stock that is negative;
  invoices must cope with over-delivery. Those specs say so when they come.
- An agent that is refused is told which rule refused it and can change that rule. That is the vision
  (the agent is a user) and a risk (owner decision 3); the history shows who did it.
- Error documents gain `rules`. It is an added member: clients that branch on `code` are unaffected.
