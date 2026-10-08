# ADR-0005: MCP server hosted in the API process over Application; CLI over HTTP

Status: accepted (2026-10-08); amended 2026-10-08 — see Amendment at the end, which overrides the CLI parts

## Context
`CLAUDE.md`: Web, CLI and MCP are thin clients over the same Application layer. MCP is the primary surface for
the product's primary user.

## Decision
MCP tools are registered in `Xerp.Api` and exposed over Streamable HTTP at `/mcp`, behind the same API-key
authentication and tenant context as the HTTP endpoints. Each tool calls exactly one Application operation
in-process. The CLI (`Xerp.Cli`) is a separate executable that calls the HTTP API. Tool naming:
`<resource>_<verb>`, snake_case; inputs/outputs use the same field names as the HTTP DTOs. Every spec lists both
signatures.

## Alternatives
- **MCP server as a separate process that calls the HTTP API.** Clean deployment separation, but doubles the
  mapping (tool -> HTTP -> operation), and two things to version. Possible later as a stdio bridge for local use.
- **Generate MCP tools from OpenAPI automatically.** Tempting, but good tool descriptions and shapes for agents
  differ from REST shapes (e.g. one `uom_get` taking id or code). Kept manual and thin; revisit if drift appears.
- **CLI linking Application directly against the database.** Bypasses authentication and tenant resolution.
  Rejected.

## Consequences
- MCP and HTTP cannot diverge in rules, only in mapping; a parity test (every operation has both) becomes possible.
- The API process is also the MCP endpoint: one deployment unit, one auth path.
- The MCP server arrives in its own spec (roadmap item 3); earlier specs carry MCP signatures as contract only.

## Amendment (2026-10-08, owner decision): CLI and web UI are separate projects
The owner decided that the CLI and the web UI are "completely unrelated and separate" projects. Therefore:
- `Xerp.Cli` is **not** part of this solution and no CLI or web UI spec will be written here. The sentence in the
  Decision above about `Xerp.Cli` is withdrawn. (The file name of this ADR is kept for stable links.)
- This repository's scope is Domain, Application, Infrastructure, Api and the MCP server hosted in the Api.
- Unchanged: MCP is hosted in the API process and calls Application in-process; tool naming; every spec lists
  HTTP and MCP signatures.
- New consequence: the HTTP API is now an external contract consumed by independently developed clients. It is
  versioned by path (`/api/v1`); within a version, changes must be backward compatible (additive fields, new
  endpoints, new error codes only where a client could not previously succeed). The OpenAPI document is the
  machine-readable description of that contract. Breaking changes need a new version or an ADR.
- Still rejected: any client reaching the database or Application directly, bypassing authentication.
