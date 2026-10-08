# ADR-0003: API keys as the first credential; tenant and actor derive from the key

Status: accepted (2026-10-08); bootstrap via config admin key confirmed by the owner on 2026-10-08

## Context
Tenant scoping needs a trustworthy source of the tenant, and "AI is a user" needs every agent to be an
identifiable actor. The scaffold has no authentication. The first clients are agents (MCP), the CLI and tests,
not browsers.

## Decision
Opaque API keys: `xerp_` + 32 random bytes (base64url), sent as `Authorization: Bearer`. Stored only as a
SHA-256 hash; shown once. A key maps to one tenant and carries `name` and `actorType` (`human` | `agent`).
Writes record the key id as `CreatedBy`/`UpdatedBy`.
Tenants are created through `/api/v1/admin/tenants`, protected by a platform admin key from configuration
(`Xerp:AdminKey`); creating a tenant returns its first key. If the admin key is not configured, admin routes
reject every request.

## Alternatives
- **JWT / OIDC from an external identity provider.** Right for human web login; heavy as a prerequisite for
  spec 001 and awkward for agents and CLI. Will be added for the web UI, mapping an OIDC subject to the same
  actor model.
- **Tenant id in a header (`X-Tenant`) without authentication.** Trivial, and trivially forgeable. Rejected.
- **ASP.NET Core Identity with username/password.** Brings user management UI concerns we do not need yet.
- **Seed a first tenant via migration or CLI command instead of an admin endpoint.** Makes multi-tenant tests
  and provisioning harder. Rejected.

## Consequences
- A leaked key is full access to one tenant until permissions (roadmap) and key revocation endpoints exist.
- SHA-256 without salt is adequate only because keys are 256-bit random values, not passwords.
- The admin key is a single shared secret in configuration: accepted by the owner as the bootstrap mechanism; revisit before
  production operations.
- `actorType` is self-declared at key creation; it is an audit label, not a security boundary.

## Amendment (2026-10-08, owner decisions)
- The CLI and the web UI are separate projects. They authenticate to the HTTP API with API keys like any other
  client. Human login for the web client (OIDC or similar) is no longer a planned item of this repository; it
  will be specified only when that project needs it.
- Agents may post documents by default (owner answered "yes" to "may agents post by default, or only prepare
  drafts?"; interpreted as "may post by default"). `actorType` therefore does not by itself restrict anything;
  restrictions come from per-key permissions (roadmap item 17).
