# ADR-0011: Partner and address model — one partner with roles, flat neutral address, unvalidated tax id

Status: accepted (2026-10-09) — first applied by spec 004. Points 3, 4 and 6 are the architect's defaults and
are listed for the owner in the hand-over of spec 004.

## Context
Spec 004 restores the two remaining scaffold masters, partner and warehouse. Both carry a postal address, and
the partner carries a tax identifier. The core is jurisdiction-neutral by owner decision (roadmap section 3):
no country-specific format, identifier or rule may be built into it. The roadmap already chose one `Partner`
record with `isCustomer` / `isSupplier` over separate customer and supplier masters; this ADR records that
choice with its alternatives and fixes the parts the roadmap left open.

## Decision
1. **One `Partner` master with two role flags**, `isCustomer` and `isSupplier`. At least one must be `true`.
   A counterparty that is both is one record with one code, one balance later (roadmap item 16).
2. **The roles are plain booleans in the same record**, not sub-records. Role-specific data (payment terms,
   price list, posting group) arrives with the specs that need it and will then decide where it lives.
3. **The address is six optional flat fields on the owning record**: `addressLine1`, `addressLine2`,
   `postalCode`, `city`, `region`, `countryCode`. One address per partner and one per warehouse. The same six
   fields, with the same rules, are used wherever a record has an address; the rules are implemented once in
   the Domain layer.
4. **`countryCode` is checked for form only**: exactly two upper-case ASCII letters (the form of ISO 3166-1
   alpha-2). It is not checked against a list of countries, and lower case is rejected, not converted.
   No other address field has a format.
5. **`taxId` is optional free text** (single line, at most 50 characters). No format, no check digit, no
   country prefix logic.
6. **`taxId` is not unique.** Two partners of one tenant may carry the same tax id (branches and departments of
   one legal entity are commonly separate partners; a missing tax id is `null` many times). The list `search`
   matches `taxId`, so an agent can check for an existing partner before creating one.
7. **Optional text is `null` when empty.** An optional text field that is omitted on create, `null`, empty or
   whitespace-only is stored and returned as `null`; the property is always present in a representation; on
   `PUT` it must be present (it may be `null`). This generalises spec 002's rule for `description`.

## Alternatives
- **Separate Customer and Supplier masters** (Business Central, ERPNext). Cleaner role-specific fields, but a
  company that buys from and sells to the same counterparty has it twice, with two codes and two addresses to
  keep equal, and netting receivables against payables needs a link between them. Rejected (roadmap, table
  "Where they differ").
- **A partner with neither role** (a lead or prospect). No operation in the roadmap would use it; it would be
  a record no document can reference. Rejected; `isActive` already covers "not in use at the moment".
- **Nested `address` object.** Reads well and repeats nothing in the schemas, but introduces questions the flat
  form does not have: `null` object versus object of `null`s, error keys with paths (`address.city`), a `PUT`
  that must say whether an omitted member clears it. Rejected for now; flat fields keep one field = one
  `errors` key = one tool argument.
- **Addresses as a sub-resource** (several per partner: billing, shipping). The right model once delivery and
  invoice addresses differ; needs its own operations and a "default" rule. Deferred to the sales/purchase
  document specs (011, 012), which will know what they need. The six fields then remain the partner's main
  address.
- **Validate `countryCode` against ISO 3166-1.** Catches `XX`, but the list changes, .NET's region data depends
  on the globalisation mode of the container, and an embedded list is one more thing to maintain. Rejected for
  the core; form validation catches the realistic agent error (a country *name* or a three-letter code).
- **Normalise `countryCode` to upper case.** Friendlier, but values are otherwise returned as entered (trim
  aside) and `type` in spec 002 already sets the precedent that an enumerated form is exact. Rejected.
- **Unique `taxId` per tenant.** Prevents duplicate partners, but is wrong for branches and makes every import
  of real data fail on the first shared id. Rejected; duplicate detection is a job for the caller (search) and
  later for a report.
- **Validate `taxId` per country** (VAT number formats, check digits). Country-specific; belongs to a
  localisation pack outside the core (roadmap, "Later").

## Consequences
- A partner cannot lose its last role. Later specs may also forbid removing a role that documents use
  (a customer with open sales orders); spec 004 gives the forward notice.
- Address data is free text: it cannot be relied on for anything but printing and reading. A consumer that
  needs a country for a rule (tax, later) must treat an absent or unknown `countryCode` as "not known".
- The partner `PUT` has twelve required-present fields and the `partner_update` tool thirteen arguments. That
  is the price of "replace means replace" (spec 001/R5); a partial-update operation is not planned.
- Partner records can contain personal data (a sole trader's name, address and tax id). They are tenant data
  like any other; they are never written to logs. Retention and erasure rules are not addressed yet.
- Contact data (e-mail, phone, contact persons) is not part of the partner yet.
