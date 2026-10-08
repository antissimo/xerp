# ADR-0002: Multi-tenancy by shared schema, TenantId column and mandatory EF query filters

Status: accepted (2026-10-08)

## Context
"Every query must be tenant-scoped." The scaffold has no tenant concept. Real systems differ here:
Business Central and SAP Business One keep each company in separate tables/databases (architect's prior
knowledge, not verified in this iteration); Odoo and ERPNext keep
companies in one schema with a `company_id`/`company` column and filter by rule (Odoo record rules plus
`check_company` consistency checks that stop a record linking to another company's record).

## Decision
One database, one schema. Every table except `Tenants` has a non-null `TenantId`. The tenant is taken only from
the authenticated credential. Isolation is enforced centrally in the DbContext (global query filter on every
`ITenantOwned` entity, stamping and cross-tenant write check on save), by tenant-prefixed unique indexes and
tenant-inclusive foreign keys, and by a model test that fails if a new entity is not tenant-owned.
A tenant is one legal entity; there is no data shared across tenants.

## Alternatives
- **Database per tenant.** Strongest isolation, simple per-tenant backup. Rejected for now: migrations and
  connection management per tenant, heavy for tests and for a product with zero tenants. Can be revisited;
  a `TenantId` column does not prevent later sharding.
- **Schema per tenant.** Same operational cost as above within one database; EF Core support is awkward.
- **PostgreSQL row-level security as the primary mechanism.** Protects even raw SQL. Deferred, not rejected:
  it needs a non-owner DB role and a per-connection tenant setting, which complicates the first spec. Planned
  as an additional layer (roadmap), after which a forgotten filter can no longer leak.
- **Tenant id passed explicitly to every query.** Relies on every developer/agent remembering it. Rejected.
- **Shared master data across tenants (Odoo-style records with no company).** Rejected: makes "other tenant's
  record" ambiguous. Common seed data is copied into each tenant instead.

## Consequences
- A missing filter is a data leak, so the filter is not optional per entity: the model test and the mandatory
  tenant-isolation test per feature are the safety net until RLS exists.
- Unique constraints are `(TenantId, ...)`; codes can repeat across tenants.
- Cross-tenant reporting/administration needs a deliberate, separate path.
- Multi-company groups inside one customer (consolidation, inter-company) are out of scope; one customer with
  two legal entities gets two tenants.
