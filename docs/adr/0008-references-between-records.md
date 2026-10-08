# ADR-0008: References between records — ids in, summaries out, dedicated error codes, restrict on delete

Status: accepted (2026-10-08) — first applied by spec 002 (article -> base unit of measure); binds every later spec

## Context
Spec 002 introduces the first record that points at another record (an article has a base unit of measure).
Every later master and document will do the same (partner, warehouse, article on a line), so the behaviour must
be decided once: how a client names the referenced record, what it gets back, what happens when the reference
is wrong, and what happens to a record that others point at. ADR-0002 already requires that a row can never
reference another tenant's row; ADR-0004 requires stable codes an agent can branch on.

## Decision
1. **Input: by server id only.** A request body names another record with a field `<role>Id` holding its UUID
   (`baseUnitId`). Codes are for people and lookups (`GET /<resource>/by-code/{code}`), not for references:
   a code can be changed by a `PUT`, an id cannot.
2. **Output: an embedded summary.** A representation returns the referenced record as an object named after the
   role, `{ "id", "code", "name" }` (`"baseUnit": { … }`), read at response time. It does not also return a
   bare `<role>Id`. An agent can therefore show or reason about the reference without a second call.
3. **A wrong reference is not a validation error.** Checks that depend on stored data are separated from
   checks on the request alone:
   - `400 VALIDATION_FAILED` — the field is missing, `null` where not allowed, or not a UUID.
   - `409 REFERENCE_NOT_FOUND` — well-formed id, but no such record in the caller's tenant. A record of another
     tenant gives exactly this response (ADR-0002: indistinguishable from missing).
   - `409 REFERENCE_INACTIVE` — the record exists but has `isActive = false` and is being *newly* assigned.
   Both 409 bodies carry `errors` keyed by the offending request field, like a validation error.
4. **Inactive means "not for new use", never "broken".** An inactive record cannot be newly referenced, but
   existing references to it stay valid and a record may be saved again while keeping such a reference.
   Deactivating a referenced record is always allowed.
5. **Delete is restricted, never cascaded.** Deleting a record that is referenced fails with `409 IN_USE`.
   Active and inactive referrers both count. The supported way to retire a referenced record is `isActive = false`.
6. **The database is the authority.** Each reference is a foreign key that includes `TenantId` on both sides,
   with `ON DELETE RESTRICT` (or `NO ACTION`). Application pre-checks give the precise error on the ordinary
   path; a foreign-key violation raised by a concurrent change is translated in Infrastructure to
   `REFERENCE_NOT_FOUND` (on insert/update of the referrer) or `IN_USE` (on delete of the target), never `500`.
7. **Order of checks** in every write operation: authentication -> request validation (`400`, all field errors
   at once) -> existence of the addressed record (`404`) -> references (`409 REFERENCE_*`) -> uniqueness
   (`409 CODE_TAKEN`). The first failing stage determines the response.

## Alternatives
- **Accept id or code (`baseUnitId` or `baseUnitCode`).** Friendlier for a human typing JSON, but every
  reference becomes an "exactly one of" rule, and a reference by code silently changes meaning when codes are
  renamed. Rejected for the contract; can be added additively later if agents demonstrably need it
  (they can call `by-code` first).
- **Return only `<role>Id`.** Smallest payload; forces one extra call per reference to make sense of a record.
  Rejected: operations should be usable without guessing or fan-out.
- **`400 VALIDATION_FAILED` with `errors.<field>` for unknown/inactive references.** One code fewer, but a
  client could tell "malformed", "does not exist" and "exists but inactive" apart only by message text, which
  ADR-0004 forbids. Rejected.
- **`404` for an unknown reference.** Ambiguous with "the addressed record does not exist". Rejected.
- **`422` for reference errors.** Semantically fine; rejected to keep one rule: `400` = the request itself is
  wrong, `409` = the request is well-formed but the tenant's current data does not permit it.
- **Cascade or set-null on delete.** Destroys history silently; unacceptable once documents exist.
- **Soft delete everywhere instead of `IN_USE`.** `isActive` already covers retirement; real deletion stays
  available for records that were created by mistake and are referenced by nothing.

## Consequences
- Two new stable codes in the registry (`REFERENCE_NOT_FOUND`, `REFERENCE_INACTIVE`); `IN_USE` gets its first use.
- Representations are not valid request bodies (`baseUnit` out, `baseUnitId` in). This was already true
  (`id`, audit fields are rejected in bodies).
- Reads of a record join its referenced records; renaming a unit is immediately visible on its articles.
- Every referenced table needs a unique key on `(TenantId, Id)` for the composite foreign key.
- `IN_USE` tells the client that referrers exist, not which; list filters by reference
  (`GET /articles?baseUnitId=`) are therefore part of every spec that adds a reference.
