# ADR-0009: MCP server shape — official SDK, stateless Streamable HTTP, tools only, API-key bearer, JSON results

Status: accepted (2026-10-08) — refines ADR-0005; first applied by spec 003.
Confirmed by the owner on 2026-10-09: MCP clients authenticate with a static API key only (no OAuth for now),
and rate limiting is not built yet (it stays on the roadmap and must precede production tenants).
SDK facts verified by the tester on 2026-10-09 (`docs/questions/003-q.md`, T-Q5), which this ADR had left
unverified: `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` 2.2.0; `WithHttpTransport(o =>
o.Stateless = true)` issues no session id and answers `GET`/`DELETE` with `405`; negotiated revision
`2026-07-28`; the generated input schema has no `additionalProperties: false` (the server must add it and
enforce it); default capabilities include `logging` (tolerated).

## Context
ADR-0005 fixed where the MCP server lives (inside `Xerp.Api`, calling Application in-process) and how tools are
named. Spec 003 has to build it, which needs decisions ADR-0005 left open: which protocol features, which
transport mode, how a tool result and a tool error look on the wire, and how MCP's own rules about
authorization, `Origin` and output schemas are met.

Checked against the MCP specification, revision **2025-11-25**, pages opened on 2026-10-08
(`/server/tools`, `/basic/transports`, `/basic/authorization` under
`https://modelcontextprotocol.io/specification/2025-11-25`) and the README of
`github.com/modelcontextprotocol/csharp-sdk`. Relevant statements found there:
- A tool result may carry `structuredContent` (a JSON object); a tool that returns it SHOULD also return the
  serialised JSON in a text block. If a tool declares an `outputSchema`, the server MUST return structured
  results conforming to it and clients SHOULD validate them.
- Errors come in two kinds: JSON-RPC *protocol errors* (unknown tool, malformed request) and *tool execution
  errors* (`isError: true`), and the latter explicitly include "input validation errors" and "business logic
  errors", because the model can act on them.
- Streamable HTTP: one endpoint; a POST is answered with `application/json` or `text/event-stream` and the
  client must accept both; sessions (`MCP-Session-Id`) are optional; a server that offers no server-initiated
  stream answers `GET` with `405`; servers MUST validate `Origin` and answer `403` when it is present and invalid.
- Authorization is OPTIONAL; when supported over HTTP it SHOULD follow the OAuth 2.1-based MCP authorization
  specification.
- The C# SDK is described as the official SDK, "maintained in collaboration with Microsoft"; packages
  `ModelContextProtocol`, `ModelContextProtocol.AspNetCore`, `ModelContextProtocol.Core`.

*Not verified:* the SDK's exact API (attribute names, the option that switches off sessions, its client
classes) and how individual MCP clients treat an error result of a tool that declares an `outputSchema`.

## Decision
1. **Library.** Use the official C# SDK (`ModelContextProtocol.AspNetCore`) rather than a hand-written JSON-RPC
   layer. The builder pins the version and reports it.
2. **Tools only.** The server declares only the `tools` capability (`listChanged: false`). No resources,
   prompts, sampling, elicitation, tasks or server-initiated messages.
3. **Stateless.** No MCP session: no `MCP-Session-Id` is issued, every POST is authenticated and handled on its
   own, `GET /mcp` and `DELETE /mcp` answer `405`. Nothing is kept in memory between requests, so any instance
   can serve any request and no session can outlive or be detached from its credential.
4. **Credential.** The same API key as the HTTP API, `Authorization: Bearer xerp_…`, on every request including
   `initialize`. Authentication failures are HTTP responses (`401`/`403` problem documents, as under
   `/api/v1`), produced before any JSON-RPC handling. The OAuth-based MCP authorization flow is **not**
   implemented: no protected-resource metadata, no `resource_metadata` in `WWW-Authenticate`.
5. **Success result.** `structuredContent` is the same JSON object the HTTP endpoint returns for the operation;
   `content` is exactly one text block holding that object serialised. Every tool declares an `outputSchema`
   describing this success object.
6. **Error result.** Every `AppError` becomes a tool execution error: `isError: true`, `content` = exactly one
   text block holding the serialised object `{ "code", "detail", "errors"? }`, and **no** `structuredContent`.
   This refines the wording of specs 001 §5 and 002 §5 ("structured content"), written before the output-schema
   rule was checked: an error object does not conform to the tool's `outputSchema`, so it must not be sent as
   `structuredContent`. The `code` values are those of the registry; nothing is renamed for MCP.
7. **Validation is a tool error, not a protocol error.** Wrong types, missing or unknown arguments reach the
   same Application validation as HTTP and come back as `VALIDATION_FAILED`. JSON-RPC errors are left for what
   the model cannot fix: unknown tool, malformed JSON-RPC.
8. **Argument shape.** A tool's arguments are the union of the HTTP operation's path parameters, query
   parameters and body fields, with the same names and JSON types (`limit` is a number, `isActive` a boolean).
   Input schemas are closed (`additionalProperties: false`).
9. **`Origin`.** A request to `/mcp` carrying an `Origin` header that is not in the configured allow-list
   (`Xerp:Mcp:AllowedOrigins`, default empty) is rejected with `403`. No CORS headers are emitted. Agents and
   server-side clients send no `Origin` and are unaffected.
10. **Deliberate exceptions to "every operation has a tool"** are listed in `docs/architecture.md` section 7 and
    need a reason there. Today: tenant provisioning (no tenant context) and API key creation (ADR-0010).

## Alternatives
- **Stateful sessions (SDK default) with a server-initiated stream.** Needed only for notifications,
  resumability and server-to-client requests, none of which a request/response ERP tool set uses; costs sticky
  routing and a session-to-key binding that must be defended. Rejected until a feature needs it.
- **Hand-written JSON-RPC endpoint.** Full control, but re-implements version negotiation and transport rules
  that change with every protocol revision. Rejected.
- **`structuredContent` on errors as well** (as spec 001 first said). Violates "MUST conform to the output
  schema" for any tool that declares one. Rejected.
- **No `outputSchema`, so errors may be structured.** Loses the typed description of results, which is exactly
  what an agent-first surface should give. Rejected.
- **`outputSchema` as a union of success and error.** Legal, but every schema becomes `anyOf`, and clients that
  generate types from schemas get unusable ones. Rejected.
- **Argument validation by the SDK / JSON-RPC `-32602`.** Gives the model an opaque protocol error and a second,
  divergent set of validation rules. Rejected (and the 2025-11-25 text sides with tool errors).
- **OAuth 2.1 per the MCP authorization specification.** The standard path for interactive clients that
  discover authorization themselves. Deferred: it needs an authorization server and human login, which this
  repository does not have (ADR-0003 amendment). Until then a client must be configured with a static header.
- **Separate MCP process over the HTTP API.** Already rejected in ADR-0005.

## Consequences
- MCP clients that support only OAuth discovery and cannot send a preconfigured `Authorization` header cannot
  connect yet. Accepted; revisit together with human login.
- A client reads errors by parsing the text block as JSON. The block's shape is contract, like the problem
  document.
- The MCP specification also says servers MUST rate-limit tool invocations. Rate limiting is not built yet
  (out of scope since spec 001); it is on the roadmap and must exist before production tenants.
- Each new operation costs one tool definition with hand-written descriptions and schemas; a test compares the
  tool list with an explicit expected list so that a missing or extra tool fails the build.
- Protocol revisions are absorbed by upgrading the SDK; the spec pins a minimum revision (the first with
  structured output), not an exact one.
