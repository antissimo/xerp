# Getting started

How to start Xerp, create a tenant, and use it over HTTP and MCP. Describes what is merged on `main` on
2026-10-09: specs 001–005 (tenants and API keys, units of measure, articles, partners, warehouses, the MCP
server, stock receipts and issues). Transfers, reversal, unit conversions, stock count and orders (006–010)
are not built yet.

**Verified** on 2026-10-09 against a fresh copy of `main` started with the commands below: every `curl` call
in sections 1–5 and the raw MCP calls in section 6 were run and answered as shown. **Not verified** here:
the client configurations in section 6 (marked there).

## 1. Start the stack

Needs Docker with Compose. Nothing else: the API image is built from source.

```bash
cp .env.example .env
# Put an admin key of at least 32 characters into .env:
sed -i "s|^XERP_ADMIN_KEY=.*|XERP_ADMIN_KEY=$(openssl rand -base64 32)|" .env

docker compose up -d --build
curl http://localhost:8000/health        # {"status":"ok","db":"ok"}
```

What you get (`compose.yaml`):

| Service | What | Reachable |
|---|---|---|
| `db` | PostgreSQL 18, data in the volume `db_data` | not published to the host — the API is the only way in |
| `api` | the Xerp API; applies database migrations at start | `http://127.0.0.1:8000` only (not from other machines) |

Settings in `.env`:

| Variable | Default | Meaning |
|---|---|---|
| `XERP_ADMIN_KEY` | empty | Platform admin key. Empty or shorter than 32 characters: the admin route rejects every request and the API logs a warning at start. |
| `XERP_API_PORT` | `8000` | Host port of the API. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | `xerp` | Database credentials, used by both services. |

Stop with `docker compose down`. `docker compose down -v` also **deletes all data**.

There is no TLS and no rate limiting: this setup is for one machine. See "Limits" at the end.

## 2. Two kinds of key

Every request carries `Authorization: Bearer <key>`.

- The **admin key** (`XERP_ADMIN_KEY`) can do one thing: create tenants. It cannot read or write any tenant's
  data (`403`).
- A **tenant API key** works inside its tenant only, on everything under `/api/v1` except the admin route,
  and on `/mcp`. Each key is `human` or `agent`; today both may do the same, and every record and posting
  says which key made it.

## 3. Create a tenant

```bash
source .env
API=http://localhost:8000/api/v1

curl -s -X POST $API/admin/tenants \
  -H "Authorization: Bearer $XERP_ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"code":"demo","name":"Demo Ltd"}'
```
```json
{ "tenant": { "id": "…", "code": "demo", "name": "Demo Ltd", "isActive": true, "createdAt": "…Z" },
  "apiKey": { "id": "…", "name": "initial", "actorType": "human", "key": "…" } }
```

`apiKey.key` is the tenant's first key. **It is shown this once and cannot be read again** — store it.

```bash
KEY='<the key from the response>'
curl -s $API/whoami -H "Authorization: Bearer $KEY"
# {"tenant":{"id":"…","code":"demo","name":"Demo Ltd"},"actor":{"apiKeyId":"…","name":"initial","actorType":"human"}}
```

## 4. Create an API key for an agent

Keys are created over HTTP only (never through MCP), by any key of the same tenant.

```bash
curl -s -X POST $API/api-keys \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"name":"claude-agent","actorType":"agent"}'
```
The response has the new key's `id` and, once, its secret in `key`. `actorType` is `human` or `agent`.

- List: `GET /api-keys` (never shows secrets).
- Revoke: `POST /api-keys/{id}/revoke`. A key cannot revoke itself.

## 5. A worked example: receive stock

Unit -> article -> warehouse -> receipt -> post -> stock on hand. References are always ids, not codes; take
each `id` from the response before it.

```bash
H="Authorization: Bearer $KEY"; C='Content-Type: application/json'

# 1. A unit of measure
curl -s -X POST $API/units-of-measure -H "$H" -H "$C" -d '{"code":"pcs","name":"Piece"}'
UNIT='<id>'

# 2. A stock article in that unit
curl -s -X POST $API/articles -H "$H" -H "$C" \
  -d "{\"code\":\"BOLT-M8\",\"name\":\"Steel bolt M8\",\"type\":\"stock\",\"baseUnitId\":\"$UNIT\"}"
ART='<id>'

# 3. A warehouse
curl -s -X POST $API/warehouses -H "$H" -H "$C" -d '{"code":"WH-1","name":"Main warehouse"}'
WH='<id>'

# 4. A receipt, as a draft: no number, no effect on stock
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"receipt\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":100}]}"
DOC='<id>'      # "status":"draft","number":null

# 5. Post it: permanent
curl -s -X POST $API/stock-documents/$DOC/post -H "$H"
# "status":"posted","number":"SR-000001","postedAt":"…Z","postedBy":"<key id>"

# 6. Stock on hand, and the ledger entry that produced it
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"
# {"items":[{"article":{…"code":"BOLT-M8"…},"warehouse":{…"code":"WH-1"…},"unit":{…"code":"pcs"…},"quantity":100}],
#  "total":1,"limit":50,"offset":0}
curl -s "$API/stock-ledger-entries?documentId=$DOC" -H "$H"
```

To take stock out, create a document with `"type":"issue"` the same way and post it. Posting an issue for
more than is on hand is refused with `409 INSUFFICIENT_STOCK`; nothing is posted.

What to know about the API:

- **Drafts and posting.** A draft can be replaced (`PUT`) and deleted. A posted document cannot be changed or
  deleted. Until spec 006 (reversal) is merged, a mistake in a posted document is corrected by posting a
  document of the opposite type.
- **Errors** are `application/problem+json` with a stable `code` (`VALIDATION_FAILED`, `NOT_FOUND`,
  `REFERENCE_NOT_FOUND`, `IN_USE`, `INVALID_STATE`, `INSUFFICIENT_STOCK`, …) and, where a field is at fault,
  `errors` keyed by field (`code`, `lines[0].quantity`). The registry is in `docs/architecture.md` §6.
- **Strict requests.** An unknown body property or query parameter is a `400`, not ignored.
- **Lists** return `{ "items", "total", "limit", "offset" }`; `limit` defaults to 50, at most 500.
- **Codes** are 1–50 letters, digits, `.`, `_` or `-`, unique per tenant.
- **Masters** in use cannot be deleted (`409 IN_USE`); deactivate them with `"isActive": false`.
- Resources so far: `/units-of-measure`, `/articles`, `/partners`, `/warehouses`, `/stock-documents`,
  `/stock-on-hand`, `/stock-ledger-entries`, `/api-keys`, `/whoami`. Their fields and rules are in
  `docs/specs/001…005`. There is no OpenAPI document yet (roadmap, 018).

## 6. Connect an MCP client

The MCP server is the same process: **`http://localhost:8000/mcp`**, Streamable HTTP transport, stateless,
authenticated with a tenant API key in the `Authorization: Bearer` header. It offers 32 tools — one per HTTP
operation (`article_create`, `stock_document_post`, `stock_on_hand_list`, …) — and a tool call does exactly
what the HTTP request does, as the tenant and actor of the key. Use an `agent` key (section 4) so that the
agent's postings are attributed to it. Without a key the endpoint answers `401`.

Checked by hand (verified):

```bash
M=http://localhost:8000/mcp
A='Accept: application/json, text/event-stream'
curl -s -X POST $M -H "Authorization: Bearer $KEY" -H "$C" -H "$A" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
curl -s -X POST $M -H "Authorization: Bearer $KEY" -H "$C" -H "$A" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"stock_on_hand_list","arguments":{}}}'
```
Answers come as one `event: message` / `data: {…}` block.

Client configuration — **not verified in this repository**; the shape below is the usual one for clients
that support remote HTTP servers with custom headers, so check your client's documentation:

```bash
# Claude Code
claude mcp add --transport http xerp http://localhost:8000/mcp \
  --header "Authorization: Bearer <agent key>"
```
```json
{ "mcpServers": {
    "xerp": { "type": "http", "url": "http://localhost:8000/mcp",
              "headers": { "Authorization": "Bearer <agent key>" } } } }
```

A client that can only start local (stdio) servers, or that insists on OAuth, cannot connect directly:
the server has API-key authentication only (OAuth is on the roadmap under "Later").

A first thing to ask the agent: *"Call whoami, then list stock on hand."*

## 7. Limits of this setup

- Local only: bound to `127.0.0.1`, plain HTTP. Do not expose it as it is.
- Every tenant key can do everything in its tenant, including posting; there are no permissions yet (017),
  no audit log of master-data changes (016), no rate limiting (020).
- The admin key and every API key are bearer secrets: keep `.env` out of version control and revoke a key
  that leaked.
- What the MVP will and will not contain: `docs/roadmap.md`, "MVP boundary".

## 8. Developing

The host needs no .NET SDK; build and tests run in a container:

```bash
scripts/dotnet.sh build
scripts/dotnet.sh test      # integration tests start their own PostgreSQL through Docker
```
