Verdict: OK

# Review — MVP cleanup, open item 001/7 (request body limit)

Reviewed with git only: `git diff main...750c3f6 -- src tests` (branch `chore/mvp-cleanup`, commits `0a06482`
tests and `750c3f6` code; six files), against row 001/7 of `docs/reviews/open-items.md`, architecture §5 and
§6, ADR-0004, ADR-0009 and the builder's notes in `docs/questions/mvp-cleanup-q.md` on the branch.
Reviewer: architect, 2026-10-09.

**The verdict is on the code as read. The suite was not run for this review**; the orchestrator runs it on
`750c3f6` and merges only if it is green. Two statements below rest on Kestrel's behaviour and not on
anything in this repository; they are marked *(Kestrel, not verified here)*.

No required changes.

## Required changes

None.

## The two decisions

**(a) The code: `413 PAYLOAD_TOO_LARGE` — confirmed.** It follows the registry's pattern (the code says what
is wrong, the status is the standard one), it is the name clients know for 413, and it is an ordinary problem
document without `errors`. The row is added to architecture §6 with this review, together with one sentence
on `/mcp`. `Problems.StatusOf` maps it in the one place that maps codes to statuses.

**(b) The limit is enforced in `ApiV1Middleware`, not by Kestrel's `MaxRequestBodySize` — accepted.** The
row said "one setting"; the aim was the bound and an answer in the error model, and the builder's reason is
sound: a limit the server enforces ends with a closed connection, so a client still sending never reads the
`413`. What was checked:

| Question | Finding |
|---|---|
| Is anything above 1 MB buffered or parsed? | No. A declared `Content-Length` above `MaxRequestBodyBytes` (1 048 576) is answered before `next` is called: no byte of the body is read by the application. A body without a length is read through `LimitedRequestBody`, which counts every read and throws once the count passes the limit; the reader (the JSON deserializer or the MCP handler) has then seen at most 1 MB plus the one read that crossed the line, and the request ends there. Nothing in `src` buffers a body (`EnableBuffering` is not used). |
| Is `/mcp` covered as well as `/api/v1`? | Yes. The check sits in the part of `ApiV1Middleware` both paths share, after the credential, and `Request.Body` is replaced before the MCP handler runs. On `/mcp` the answer is an HTTP problem document, as `401` and `403` are (ADR-0009), not a tool error. `/health` is outside the middleware's two paths and is not limited — intended. |
| Does the exception reach the error model? | Yes. `JsonBody` catches `JsonException` only, so the `BadHttpRequestException` (status 413) passes it; `ErrorHandlingMiddleware` is registered outside `ApiV1Middleware` and answers `413 PAYLOAD_TOO_LARGE` for that status, `400 VALIDATION_FAILED` for any other unreadable request as before. The tool-level `catch (Exception)` in `McpServerSetup` is not on this path: the body is read before any tool is invoked. |
| Is the unread rest discarded with a bound? | The application drains nothing: there is no read loop in the diff. What is discarded is discarded by the server after the response, as for any request whose body was not read: for at most about 5 seconds and never beyond its own body limit of 30 MB, after which it closes the connection *(Kestrel, not verified here — neither value is set or tested in this repository)*. The 4 MB chunked case on a real listener shows the client reads the `413` while still sending. |
| Are legitimate bodies unaffected? | Yes. A body with a length at or below the limit is not wrapped at all: the path is byte for byte the old one. A body without a length is wrapped by a stream that only counts. Tests: exactly 1 048 576 bytes is accepted declared and chunked on `/api/v1` (`201`) and declared on `/mcp` (`200`). The other 1 464 − 4 integration tests exercise the unchanged path. |
| Order of checks | Credential first (`401`, `403`), then size, then routing — so an unauthenticated request's body is never looked at, and an oversized request to an unknown path is `413`. Consistent with architecture §5. Tested (`The_credential_is_judged_before_the_size_of_the_body`). |
| Tenant scope, layers | No query, no data. `AppError.PayloadTooLarge` and its code are in Application with every other code; the limit, the stream and the status are in Api. |

## Builder's notes (`mvp-cleanup-q.md`, B-Q1 – B-Q4)

- **B-Q1** (the code): confirmed, row added — decision (a).
- **B-Q2** (1 048 576 bytes, inclusive, a constant; `/api/v1` with admin routes and `/mcp`, not `/health`):
  confirmed. A setting is not needed until someone has a legitimate body near the limit.
- **B-Q3** (credential before size, size before routing; problem document on `/mcp`): confirmed.
- **B-Q4** (limit in the Api layer): confirmed — decision (b).

## Tests

`RequestBodyLimitTests`, four tests, written on the branch before the code (`0a06482`): declared and chunked
bodies one byte above the limit and 4 MB chunked on `/api/v1` (POST and PUT), declared and chunked on `/mcp`,
exactly the limit on both, nothing applied by a refused request, credential before size on five combinations,
`/health` not limited. Three run on a real Kestrel port, which is where the difference from a server-side
limit shows. More than the "one test" the row asked for, and the right ones. No existing test was changed.

## Non-blocking

1. **The bound on discarding the rest of a refused body is Kestrel's default, not ours.** A holder of a key
   can still make the server receive and throw away up to 30 MB per refused request, for up to about 5
   seconds *(Kestrel, not verified here)*. That is network and time, not memory or parsing, which is what
   001/7 was about. If it is to be tightened, set `MaxRequestBodySize` a little above 1 MB as a second,
   outer bound — with rate limiting (020). Open item cleanup/1, after MVP.
2. `LimitedRequestBody` also wraps requests that have no body and no `Content-Length` (most `GET`s).
   Harmless: nothing is read.
3. The limit is per request; many parallel 1 MB requests are a matter for rate limiting (020), as before.
4. `LimitedRequestBody.Read` (synchronous) is never reached under Kestrel, which refuses synchronous reads;
   it is there for completeness.

## For the merge

- Merge `chore/mvp-cleanup` at `750c3f6` if the orchestrator's run on it is green (0 failed, 0 skipped).
  Any commit after `750c3f6` that touches `src` needs a look at its diff.
- `docs/architecture.md` and `docs/reviews/open-items.md`: take `main`'s version. `docs/questions/mvp-cleanup-q.md`
  comes from the branch; the answers to it are in this file.
- With this merged, no before-MVP item is open.
