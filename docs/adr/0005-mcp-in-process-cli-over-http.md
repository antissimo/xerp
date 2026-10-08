# ADR-0005: MCP server hosted in the API process over Application; CLI over HTTP

Status: accepted (2026-10-08)

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
