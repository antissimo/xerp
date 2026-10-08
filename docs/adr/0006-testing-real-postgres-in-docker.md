# ADR-0006: Integration tests against real PostgreSQL via Testcontainers; SDK runs in Docker

Status: accepted (2026-10-08) — the exact docker invocation is unverified until spec 001 is built.
Amended 2026-10-09: from spec 003 on the acceptance tests are written by a separate tester agent before the
builder implements (`tester.md`); acceptance criteria are therefore black-box, and criteria that need access
below the public surface are marked *(builder)* (architecture §9).

## Context
The host has no .NET SDK; only Docker with `mcr.microsoft.com/dotnet/sdk:10.0` and `postgres:18`.
Tenant isolation depends on query filters, composite unique indexes and case-insensitive matching — behaviour
that in-memory providers do not reproduce.

## Decision
xUnit. Unit tests without I/O. Integration tests run the real API in-process with `WebApplicationFactory`
against a PostgreSQL 18 container started by Testcontainers, with real migrations. `dotnet` runs inside the SDK
container with the host Docker socket mounted, through a wrapper script delivered by spec 001.
Tests isolate themselves by creating their own tenants, not by resetting the database.

## Alternatives
- **EF Core InMemory / SQLite.** Fast, no Docker, but does not enforce the constraints we rely on. Rejected.
- **A fixed `db-test` service in compose.** Avoids socket mounting, but is a shared mutable database with
  manual lifecycle; `builder.md` requires a per-run database. Kept as the fallback if socket access is impossible.
- **Install the SDK on the host.** Owner's machine, owner's call; not assumed.

## Consequences
- Tests need Docker socket access from inside a container (effectively root on the host for the test run).
- First run pulls NuGet packages and the Testcontainers helper image; needs network.
- Per-test tenants make tests parallel-safe and double as constant exercise of tenant isolation.
