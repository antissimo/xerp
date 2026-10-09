# Getting started

How to start Xerp, create a tenant, and use it over HTTP and MCP. Describes what is merged on `main` on
2026-10-09 (`67dba0e`): the whole MVP, specs 001–010 — tenants and API keys, units of measure, articles,
partners, warehouses, the MCP server, stock receipts, issues, transfers, reversal, unit conversions, stock
count, purchase orders with goods receipt and sales orders with delivery.

**Verified** on 2026-10-09 against a throwaway copy of `main` (`2628860`) started with the commands of
section 1 (with `XERP_API_PORT=8011` instead of 8000, the only difference): every `curl` call in the code
blocks of sections 1–8 and the raw MCP calls in section 9 were run in the order shown, and every response
fragment, number and quantity quoted here is what came back.

**Not verified in that run:** the client configurations in section 9; listing and revoking API keys
(section 4, checked for the earlier version of this guide only); and rules stated in prose without a quoted
result — they are taken from the specs, and the ones not exercised are marked *(not run)*.

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

Each stock-on-hand item also has `incomingQuantity`, `reservedQuantity` and `availableQuantity`; they are
`0`, `0` and `100` here and are explained in sections 7 and 8.

To take stock out, create a document with `"type":"issue"` the same way and post it. Posting an issue for
more than is on hand is refused with `409 INSUFFICIENT_STOCK`; nothing is posted and the draft stays.

## 6. More stock documents: transfer, reversal, unit conversion, count

All four use the routes of section 5; only the body differs. The examples continue from section 5
(100 `pcs` of `BOLT-M8` in `WH-1`).

### 6.1 Transfer between warehouses

```bash
curl -s -X POST $API/warehouses -H "$H" -H "$C" -d '{"code":"WH-2","name":"Shop"}'
WH2='<id>'

# warehouseId is where the stock leaves, toWarehouseId where it arrives
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"transfer\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",\"toWarehouseId\":\"$WH2\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":30}]}"
TR='<id>'
curl -s -X POST $API/stock-documents/$TR/post -H "$H"       # "number":"ST-000001"

curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"         # WH-1: 70, WH-2: 30
curl -s "$API/stock-ledger-entries?documentId=$TR" -H "$H"  # two entries: -30 in WH-1, 30 in WH-2
```

### 6.2 Reverse a posted document

A posted document is never edited. `reverse` posts a second document of the same type that undoes it, and
marks the original `reversed`. The body needs the reversal's own `documentDate` (without it: `400`, key
`documentDate`), which may not be earlier than the original's *(not run)*; `note` is optional.

```bash
curl -s -X POST $API/stock-documents/$TR/reverse -H "$H" -H "$C" \
  -d '{"documentDate":"2026-10-09","note":"Sent to the wrong shop"}'
# 201: the reversing document — "status":"posted","number":"ST-000002","reversalOf":{…"number":"ST-000001"}

curl -s $API/stock-documents/$TR -H "$H"              # "status":"reversed","reversedBy":{…"number":"ST-000002"}
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"   # WH-1: 100; WH-2 has nothing and is no longer listed
```

A document is reversed once (`409 INVALID_STATE` the second time), and whole: there is no partial reversal.
A reversal that would take stock below zero is refused with `409 INSUFFICIENT_STOCK` *(not run)*.

### 6.3 A second unit for an article

Stock is always kept in the article's base unit. An article can be given further units, each with a factor
to the base unit; a document or order line may then use any of them.

```bash
curl -s -X POST $API/units-of-measure -H "$H" -H "$C" -d '{"code":"box","name":"Box"}'
BOX='<id>'

# 1 box of BOLT-M8 = 50 pcs. PUT creates the conversion (201 here) or changes its factor.
curl -s -X PUT $API/articles/$ART/units/$BOX -H "$H" -H "$C" -d '{"factor":50}'
curl -s $API/articles/$ART/units -H "$H"

# Receive 2 boxes
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"receipt\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":2,\"unitId\":\"$BOX\"}]}"
DOC2='<id>'     # the line shows "quantity":2,"factor":50,"baseQuantity":100
curl -s -X POST $API/stock-documents/$DOC2/post -H "$H"     # "number":"SR-000002"
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"         # WH-1: 200 (pcs)
```

A line without `unitId` is in the base unit. The factor a line was saved with stays on the line.

### 6.4 Stock count

A count lists what was counted of each article in one warehouse; posting writes the difference. On empty
stock this is how opening balances are entered.

```bash
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"count\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":195}]}"
CNT='<id>'      # the line shows "bookQuantity":200,"differenceQuantity":-5
curl -s -X POST $API/stock-documents/$CNT/post -H "$H"      # "number":"SC-000001"

curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"         # WH-1: 195
curl -s "$API/stock-ledger-entries?documentId=$CNT" -H "$H" # one entry: -5
```

`quantity` on a count line is the counted quantity and may be `0`. The book quantity is taken when the draft
is saved; if stock of a counted article moved before posting, the post is refused with `409 COUNT_OUTDATED` —
save the draft again (`PUT`) and post *(not run)*. Articles not on the count are untouched.

## 7. Purchase flow: order -> confirm -> linked receipt -> post

```bash
# 1. A supplier
curl -s -X POST $API/partners -H "$H" -H "$C" -d '{"code":"SUP-1","name":"Fasteners Inc","isSupplier":true}'
SUP='<id>'

# 2. A purchase order as a draft: 10 boxes at 12.50 each, to WH-1
curl -s -X POST $API/purchase-orders -H "$H" -H "$C" \
  -d "{\"orderDate\":\"2026-10-09\",\"supplierId\":\"$SUP\",\"warehouseId\":\"$WH\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":10,\"unitId\":\"$BOX\",\"unitPrice\":12.50}]}"
PO='<id>'       # "status":"draft","number":null,"totalAmount":125; the line: "lineNo":1,"baseQuantity":500

# 3. Confirm it: permanent, gets its number
curl -s -X POST $API/purchase-orders/$PO/confirm -H "$H"
# "status":"confirmed","number":"PO-000001"; the line: "outstandingBaseQuantity":500
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"
# "quantity":195,"incomingQuantity":500 — ordered, not yet received

# 4. A goods receipt is a receipt linked to the order: purchaseOrderId on the document,
#    orderLineNo on every line. Here 4 of the 10 boxes arrive.
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"receipt\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",\"purchaseOrderId\":\"$PO\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":4,\"unitId\":\"$BOX\",\"orderLineNo\":1}]}"
GR='<id>'

# 5. Post it
curl -s -X POST $API/stock-documents/$GR/post -H "$H"       # "number":"SR-000003"

curl -s $API/purchase-orders/$PO -H "$H"
# "receiptStatus":"partial"; the line: "receivedBaseQuantity":200,"outstandingBaseQuantity":300
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"         # "quantity":395,"incomingQuantity":300
```

- A draft order can be replaced (`PUT`) and deleted; a confirmed one cannot be changed
  (`409 INVALID_STATE`). To change what was ordered, close the order and create a new one.
- The receipt must be for the order's warehouse and each line for the article of its order line. Receipts
  may be partial and several; a posting that would take a line above the ordered quantity is refused with
  `409 QUANTITY_EXCEEDS_ORDER` (tried here with 7 more boxes: "350 … 300 is outstanding").
- When nothing more will come: `POST /purchase-orders/$PO/close` -> `"status":"closed"`, the line's
  `outstandingBaseQuantity` and the pair's `incomingQuantity` become `0`. `…/reopen` undoes a close. A receipt
  for a closed order is refused with `409 ORDER_NOT_OPEN` *(not run)*.
- Reversing the receipt (6.2) gives the received quantity back to the order *(not run for a receipt; run for
  a delivery in section 8)*.
- Prices are recorded and totalled (`lineAmount`, `totalAmount`, without tax). Nothing is valued or invoiced.

## 8. Sales flow: order -> confirm -> linked delivery -> post

The mirror of section 7, run after it (the purchase order was closed, so 395 on hand, nothing incoming).

```bash
# 1. A customer
curl -s -X POST $API/partners -H "$H" -H "$C" -d '{"code":"CUS-1","name":"Builders Ltd","isCustomer":true}'
CUS='<id>'

# 2. A sales order as a draft: 120 pcs at 0.40. A draft is an offer and reserves nothing.
curl -s -X POST $API/sales-orders -H "$H" -H "$C" \
  -d "{\"orderDate\":\"2026-10-09\",\"customerId\":\"$CUS\",\"warehouseId\":\"$WH\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":120,\"unitPrice\":0.40}]}"
SO='<id>'       # "status":"draft","number":null,"totalAmount":48

# 3. Confirm it: permanent, gets its number, reserves
curl -s -X POST $API/sales-orders/$SO/confirm -H "$H"       # "status":"confirmed","number":"SO-000001"
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"
# "quantity":395,"reservedQuantity":120,"availableQuantity":275

# 4. A delivery is an issue linked to the order: salesOrderId on the document, orderLineNo on every line
curl -s -X POST $API/stock-documents -H "$H" -H "$C" \
  -d "{\"type\":\"issue\",\"documentDate\":\"2026-10-09\",\"warehouseId\":\"$WH\",\"salesOrderId\":\"$SO\",
       \"lines\":[{\"articleId\":\"$ART\",\"quantity\":50,\"orderLineNo\":1}]}"
DL='<id>'

# 5. Post it
curl -s -X POST $API/stock-documents/$DL/post -H "$H"       # "number":"SI-000001"

curl -s $API/sales-orders/$SO -H "$H"
# "deliveryStatus":"partial"; the line: "deliveredBaseQuantity":50,"outstandingBaseQuantity":70
curl -s "$API/stock-on-hand?articleId=$ART" -H "$H"
# "quantity":345,"reservedQuantity":70,"availableQuantity":275
```

- **Reserved and available are information, not a lock.** `reservedQuantity` is what confirmed sales orders
  still have to deliver from the warehouse; `availableQuantity` is `quantity − reservedQuantity` and can be
  negative. Confirming an order is never refused for lack of stock: a second order for 1000, confirmed
  after the reversal below, gave `"reservedQuantity":1120,"availableQuantity":-725`. An unlinked issue can
  take reserved stock *(not run)*.
- **Stock is checked when the delivery is posted**: more than is on hand -> `409 INSUFFICIENT_STOCK`
  *(not run for a linked delivery; run for a plain issue in section 5)*; more
  than the order line still has outstanding -> `409 QUANTITY_EXCEEDS_ORDER` (tried with 71 against 70).
- Close, reopen, the rule for a confirmed order and reversal work as in section 7. Reversing the delivery
  here gave `SI-000002`, stock back to 395 and the order back to `"deliveryStatus":"none"`, 120 reserved.
- Orders are also read by number: `GET /purchase-orders/by-number/PO-000001`,
  `GET /sales-orders/by-number/SO-000001` *(the purchase one was run)*; lists filter by `status` (run:
  `?status=closed`), partner, warehouse and progress *(not run)*.

What to know about the API:

- **Drafts and posting.** A draft can be replaced (`PUT`) and deleted. A posted document cannot be changed or
  deleted; a mistake is corrected by reversing it (6.2) and posting a new document. Orders follow the same
  idea: draft -> confirmed (permanent) <-> closed.
- **Numbers** are given at posting or confirmation, never to a draft, per tenant and without gaps: `SR-`
  receipts, `SI-` issues, `ST-` transfers, `SC-` counts, `PO-` purchase orders, `SO-` sales orders.
- **Errors** are `application/problem+json` with a stable `code` (`VALIDATION_FAILED`, `NOT_FOUND`,
  `REFERENCE_NOT_FOUND`, `IN_USE`, `INVALID_STATE`, `INSUFFICIENT_STOCK`, `COUNT_OUTDATED`,
  `QUANTITY_EXCEEDS_ORDER`, `ORDER_NOT_OPEN`, …) and, where a field is at fault,
  `errors` keyed by field (`code`, `lines[0].quantity`). The registry is in `docs/architecture.md` §6.
- **Strict requests.** An unknown body property or query parameter is a `400`, not ignored.
- **Lists** return `{ "items", "total", "limit", "offset" }`; `limit` defaults to 50, at most 500.
- **Codes** are 1–50 letters, digits, `.`, `_` or `-`, unique per tenant.
- **Masters** in use cannot be deleted (`409 IN_USE`); deactivate them with `"isActive": false`.
- Resources of the MVP: `/units-of-measure`, `/articles`, `/articles/{id}/units`, `/partners`, `/warehouses`,
  `/stock-documents`, `/stock-on-hand`, `/stock-ledger-entries`, `/purchase-orders`, `/sales-orders`,
  `/api-keys`, `/whoami`. Their fields and rules are in `docs/specs/001…010`. There is no OpenAPI document yet (roadmap, 018).

## 9. Connect an MCP client

The MCP server is the same process: **`http://localhost:8000/mcp`**, Streamable HTTP transport, stateless,
authenticated with a tenant API key in the `Authorization: Bearer` header. It offers 53 tools — one per HTTP
operation (`article_create`, `stock_document_post`, `stock_document_reverse`, `article_unit_set`,
`purchase_order_confirm`, `sales_order_create`, `stock_on_hand_list`, …; creating API keys is HTTP only) — and a tool call does exactly
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
curl -s -X POST $M -H "Authorization: Bearer $KEY" -H "$C" -H "$A" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sales_order_get","arguments":{"number":"SO-000001"}}}'
```
`tools/list` returned 53 tools.
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

A first thing to ask the agent: *"Call whoami, then list stock on hand."* Then, for example: *"Order 10 boxes
of BOLT-M8 from SUP-1 to WH-1, confirm the order and receive 4 of them."*

## 10. Limits of this setup

- Local only: bound to `127.0.0.1`, plain HTTP. Do not expose it as it is.
- Every tenant key can do everything in its tenant, including posting; there are no permissions yet (017),
  no audit log of master-data changes (016), no rate limiting (020).
- The admin key and every API key are bearer secrets: keep `.env` out of version control and revoke a key
  that leaked.
- **No money side.** Prices on orders are recorded and totalled, nothing else: no invoices, no tax, no
  valuation of stock, no accounting, no payments.
- What the MVP does and does not do: `docs/roadmap.md`, "MVP boundary".

## 11. Developing

The host needs no .NET SDK; build and tests run in a container:

```bash
scripts/dotnet.sh build
scripts/dotnet.sh test      # integration tests start their own PostgreSQL through Docker
```
