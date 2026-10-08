# ADR-0010: API key lifecycle — create, revoke, never delete; no self-revocation; secrets never through MCP

Status: accepted (2026-10-08) — extends ADR-0003; first applied by spec 003.
Confirmed by the owner on 2026-10-09 as written, including point 5 (no `api_key_create` MCP tool) and the
consequence that any tenant key may manage keys until the permissions spec (roadmap item 6).

## Context
Since spec 001 a tenant has exactly one key (`initial`, `human`), created with the tenant. An agent can only be
a distinguishable actor (vision principle 4) if it has its own key, so key management must arrive with the MCP
server. There are no permissions yet (roadmap item 6): every key can do everything inside its tenant, including
managing keys.

## Decision
1. **Operations:** create, list, get, revoke — tenant-scoped, under `/api/v1/api-keys`. No rename, no edit.
2. **Revocation is permanent.** `isActive` goes from `true` to `false` once; there is no reactivation. A key
   that may have leaked must not come back; issuing a new key is cheap.
3. **Keys are never deleted.** `createdBy` / `updatedBy` on every record and (later) the audit log point at key
   ids; the row must outlive the credential.
4. **A key cannot revoke itself** (`409 CANNOT_REVOKE_SELF`), and revocation is made safe under concurrency so
   that two keys cannot revoke each other at the same moment. Consequence: a tenant always keeps at least one
   active key and cannot lock itself out. (There is no admin operation that issues a key for an existing tenant.)
5. **The plaintext key is returned only by HTTP.** There is no `api_key_create` MCP tool; `api_key_list`,
   `api_key_get` and `api_key_revoke` exist. A tool result is copied into a model's context, into transcripts
   and often into third-party logs — places a credential must never be. An agent with HTTP access can still
   create a key (principle 2 is about capability, and nothing is forbidden to an agent that is allowed to a
   human); the secret just does not travel through a channel built to be read by a model.
6. **Provenance:** a key records `createdBy` (the key that created it; `null` for a tenant's `initial` key),
   `revokedAt` and `revokedBy`.
7. **`actorType` is chosen at creation and cannot change** (ADR-0003: an audit label, not a security boundary).

## Alternatives
- **Reactivation / toggle `isActive`.** Convenient; turns every leak into "was it re-enabled since?". Rejected.
- **Hard delete.** Breaks attribution of past writes. Rejected.
- **Allow self-revocation.** Natural for "log me out", but with no admin recovery path the last key could
  remove itself and orphan the tenant. Rejected until an admin recovery operation exists.
- **Only `human` keys may manage keys.** Would make `actorType` a security boundary, which ADR-0003 says it is
  not, and it is self-declared anyway. Real restriction comes with permissions (roadmap item 6). Rejected.
- **`api_key_create` as an MCP tool.** Full parity, and lets an orchestrating agent provision sub-agents by
  itself. Rejected because of point 5 (owner confirmed 2026-10-09); can be reconsidered behind a permission.
- **Expiry dates, rotation, scopes.** Useful; belong to the permissions spec. Not now.

## Consequences
- Until permissions exist, any key — including an agent's — can create further keys over HTTP and revoke other
  keys. This is the same trust level as today ("a key is full access to its tenant"), now including key
  management. Roadmap item 6 must be able to remove these rights per key.
- The key list grows forever; `isActive` and `actorType` filters keep it usable.
- Key names are labels, not identifiers: they may repeat (a revoked `claude-warehouse` can be followed by a new
  `claude-warehouse`).
