# ADR-0019: The default warehouse, and a warehouse on every document

Status: accepted (2026-10-10) — owner request for spec 011. Decisions 1 and 6 are the owner's; 2–5 and 7 are
the architect's defaults and are listed for the owner in spec 011 §12.

## Context
Owner, 2026-10-10: a tenant may have several warehouses but must have a central one from the start, and
every document — receipt, invoice, order — is made on a warehouse. As built, a new tenant has no warehouse
(spec 004 put a default warehouse out of scope) and every stock document and order requires the caller to
name one, so the first thing any new tenant or agent must do is invent a warehouse.

## Decision
1. **A tenant is created with one warehouse, its default**, in the same transaction as the tenant and its
   first key. There is no moment at which a tenant exists without it.
2. **"Default" is a flag on a warehouse (`isDefault`), not a property of a code or of the first row.**
   Exactly one warehouse per tenant has it, and that warehouse is active — enforced by the database as well.
   The initial code `CENTRAL` and name "Central warehouse" are only what the row starts with.
3. **The default warehouse is otherwise ordinary** — renamed, re-coded, addressed, used by documents like
   any other — except that it cannot be deactivated or deleted while it is the default
   (`409 DEFAULT_WAREHOUSE`).
4. **The tenant can move the flag** to another active warehouse with one action (`set-default`); the former
   default becomes ordinary and can then be retired. Existing documents keep their warehouse.
5. **Existing tenants:** the oldest active warehouse becomes the default; a tenant with none gets a new one.
6. **Every document type has a mandatory warehouse in its header** (a transfer: two). This holds for the
   types that exist and is the rule for every type added later; invoices are the first.
7. **The caller need not name it on create:** an omitted `warehouseId` means the default warehouse at that
   moment (for a stock document linked to an order: the order's warehouse). The resolved warehouse is stored
   and returned; nothing remembers that it was defaulted. On replace, and for both warehouses of a transfer,
   the caller names the warehouse.

## Alternatives
- **No default; keep requiring `warehouseId`.** Explicit, but contradicts the owner's request and makes the
  single-warehouse tenant — the common small company — repeat one id in every call.
- **The default is "the warehouse with code `CENTRAL`"** (or the oldest one). No flag, but then renaming is
  dangerous or forbidden, and the default can never be moved. A flag costs one column.
- **The default as a tenant setting** (`Tenant.DefaultWarehouseId`). Equivalent in effect; but tenants are
  managed by the admin key and have no tenant-side update operation, and a nullable reference from the
  tenant row to a tenant-owned table reverses the direction every other key has. The flag stays inside the
  resource the tenant already manages.
- **An immovable default** (no `set-default`). Simpler, but the first warehouse could never be closed —
  a company that moves would keep a dead warehouse as its default for ever.
- **Allow deactivating the default and fall back to another.** Then "the default" is whatever the fallback
  rule says that day, and an omitted `warehouseId` becomes unpredictable. One rule instead: there is always
  exactly one, it is active, and changing it is explicit.
- **Omission also on replace and on transfers.** On `PUT` every field is present by convention (architecture
  §5) and the caller has just read the document; a transfer is *about* two warehouses, and defaulting one of
  them would make "equal source and destination" depend on the database instead of on the request.
- **A default warehouse per API key or per partner.** A finer convenience that needs permissions and settings
  the system does not have; can be layered on later — the tenant default remains the last fallback.
- **Documents without a warehouse where it means nothing** (an invoice for services). Rejected for now on the
  owner's wording; a later spec that needs it must ask.

## Consequences
- A new tenant can post its first receipt with a unit and an article alone; getting started needs one step
  fewer, and an agent can always find a valid warehouse (`isDefault`).
- `GET /warehouses` of a new tenant is no longer empty; earlier tests that assumed so change (011/AC-01).
- Loosening `warehouseId` on create is compatible for existing clients: every request that was valid stays
  valid and means the same.
- One new error code, `DEFAULT_WAREHOUSE`, and one invariant ("exactly one active default") that set-default
  and warehouse replace/delete must keep under concurrency.
- The invoice spec (015) starts with a warehouse on the header and the same omission rule; so does any other
  later document type.
