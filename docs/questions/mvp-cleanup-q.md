# MVP cleanup — questions and notes

## From the builder (branch `chore/mvp-cleanup`)

The one required item, 001/7 of `docs/reviews/open-items.md`, is built. B-Q1 to B-Q4 are choices the row left
open, for the architect to confirm or correct.

### B-Q1. The code is `PAYLOAD_TOO_LARGE`; it is not in the registry yet
The row says "`413` in the error model" and that the architect adds the status and code to architecture §6.
The registry on `main` (`1a25a92`) has no `413` row, so I chose the code: `413 PAYLOAD_TOO_LARGE`, an ordinary
problem document (`type`, `title`, `status`, `detail`, `code`), no `errors`. `detail` names the limit in bytes.
If another name is wanted it is one constant (`ErrorCodes.PayloadTooLarge`) and one constant in the test.
Asked of the **architect**: add the row to §6, or name the code.

### B-Q2. The limit is 1 048 576 bytes, inclusive
A body of exactly 1 048 576 bytes is accepted, one byte more is refused. It is a constant
(`ApiV1Middleware.MaxRequestBodyBytes`), not a setting. It applies to `/api/v1` (admin routes included) and
`/mcp`, not to `/health`.

### B-Q3. Order: the credential is judged first
`401` and `403` are answered before the size is looked at (architecture §5: the credential is checked before
the route); the body of an unauthenticated request is never read. `413` comes before routing, so an oversized
request to an unknown path is `413`, not `404`. On `/mcp` it is an HTTP problem document, as `401` and `403`
are (ADR-0009), not a tool error.

### B-Q4. The limit is kept in the Api layer, not by Kestrel's `MaxRequestBodySize`
The row says "one setting". I tried it first: Kestrel refuses the body and closes the connection, and a client
that is still sending a chunked body gets a broken connection in place of the `413` (the test on a real
listener failed that way every time). So: a declared `Content-Length` above the limit is refused unread in
`ApiV1Middleware`; a body without a length is counted while it is read (`LimitedRequestBody`, 40 lines) and
refused when it passes the limit. Nothing above 1 MB is buffered or parsed. What the server still does is
discard the unread rest of a refused body, as it does for any request whose body the application did not
read, within Kestrel's own bounds (30 MB, about 5 seconds), so that the client can read the answer.

### Tests
`RequestBodyLimitTests` (4): `/api/v1` and `/mcp`, declared and chunked, at the limit and one byte above, 4 MB
chunked, credential before size, `/health` not limited. Three of them run against a second host on a real
Kestrel port (`WebApplicationFactory.UseKestrel`), one against the in-process host.
Suite: unit 534 passed; integration 1464 passed, 0 failed, 0 skipped.
