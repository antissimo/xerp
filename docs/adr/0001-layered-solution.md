# ADR-0001: Four-project layered solution; Application may use EF Core abstractions

Status: accepted (2026-10-08)

## Context
`CLAUDE.md` prescribes Domain -> Application -> Infrastructure -> Api with Web, CLI and MCP as thin clients.
The scaffold is one project where controllers talk to the DbContext. Three client surfaces are coming; any rule
left in a controller would have to be duplicated in MCP tools.

## Decision
Split into `Xerp.Domain`, `Xerp.Application`, `Xerp.Infrastructure`, `Xerp.Api` (+ `tests/`), with the dependency
table in `docs/architecture.md` section 2, enforced by a unit test over assembly references.
Application reaches the database through an interface it owns (`IXerpDb`, exposing `DbSet<T>` and
`SaveChangesAsync`), so it references the `Microsoft.EntityFrameworkCore` package but not Npgsql, not the
concrete DbContext and not ASP.NET Core. Use cases are plain classes; no mediator library.

## Alternatives
- **Keep one project, organise by folders.** Least work, but nothing stops a controller from holding rules, and
  MCP/CLI could not reuse them. Rejected: contradicts the project rules.
- **Repository interfaces per aggregate, Application free of EF Core.** Purest, but adds a repository and a
  query-specification layer for every entity; list/search/paging would be reimplemented by hand. Rejected for
  now as cost without a second persistence technology in sight.
- **Vertical-slice modules (one project per business module).** Attractive later for a large ERP; premature with
  four entities. Can be introduced inside the layers as folders (`Application/Inventory/...`).
- **MediatR / pipeline library.** Extra dependency and indirection for no present need.

## Consequences
- The existing `api/` project is dissolved in spec 001; Dockerfile and compose move to the new layout.
- Application code can use LINQ and async EF operators directly; queries are tested against real PostgreSQL.
- If EF Core must ever be replaced, Application queries have to be rewritten. Accepted.
- PostgreSQL-specific behaviour (constraint violations, case-insensitive matching) must be translated in
  Infrastructure into Application-level errors, because Application cannot see Npgsql types.
