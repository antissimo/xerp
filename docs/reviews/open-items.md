# Open non-blocking items — reviews 001 to 009

Consolidated by the architect, 2026-10-09. Sources: the "Non-blocking" sections of `docs/reviews/001.md` …
`006.md` and the builder's notes (005-q and 006-q, B-Q9); items 007/1 and 007/2 were added with review 007; review 008 closed 006/2 and added no item; review 009 closed 004/4 and 005/3 on `12f33ff` (not yet on `main`: review 009 waits for two test corrections by the tester) and added 009/1. Each item was checked against the code on `main`
(`4fc742e`) and, for review 006, on `db01f2c`. This list is the input for one cleanup task for the builder
after spec 010; it replaces the "still open from earlier reviews" lines in the individual reviews.

Not in this list: required change 1 of review 006 (inactive header masters reported alone) — that is a
required change, in progress, not a non-blocking item.

Status: **open** (nothing done), **partly** (some of it done), **done**, **in a spec** (open, but an MVP spec
already requires it, so it is done there and not in the cleanup task), **no action** (a remark or a thing to
watch; there is nothing to build).

Call: **before MVP** only for correctness, security or data integrity. Everything else is **after MVP**.

## Summary

| | Count |
|---|---:|
| Items looked at | 34 |
| Done | 8 |
| No action (remarks) | 6 |
| Open or partly open | 20 |
| — of these, before MVP in the cleanup task | 1 |
| — before MVP, checked in a review (review 010) | 1 |
| — after MVP | 18 |

**The cleanup task after spec 010 has one item: 001/7, a request body limit.**
Before the MVP is declared finished, one more must be true, and it belongs to the reviews: 005/2 (checked in
every review; done up to spec 009, pending for 010). 006/2 is done (spec 008, `41d275b`); 004/4 and 005/3 are
done (spec 009, `12f33ff`).

## Items

| Source | What | Status on `main` | Call | Reason |
|---|---|---|---|---|
| 001/1 | `IXerpDb` exposes the unfiltered `Tenants` set | done (spec 003: `ICurrentTenantReader`) | — | |
| 001/2 | Test with a non-ASCII case pair for code uniqueness | done (re-review 001) | — | |
| 001/3 | `401` from an operation lacks `WWW-Authenticate: Bearer` | done (spec 003, B-Q6) | — | |
| 001/4 | An admin key longer than 512 characters is silently unusable (`CredentialResolver.MaxTokenLength`); the start-up warning does not say so | open | after MVP | Fails closed: such a key is rejected, never accepted wrongly. A configuration nuisance for the operator only. |
| 001/5 | Constraint names in Application (`DbNames`) instead of violations reported by meaning | partly: code clashes are recognised by meaning (`IsCodeOf<T>()`); two comparisons by name remain (`TenantCodeIndex` in `TenantProvisioning`, `ArticleBaseUnitForeignKey` in `ArticleOperations`) | after MVP | Structure only; both remaining cases behave correctly and are tested. |
| 001/6 | The `404` for an unsupported method keeps routing's `Allow` header | open (`ApiV1Middleware` rewrites the status, not the header) | after MVP | Harmless; decide "keep" or "clear" when the OpenAPI document (018) is written. |
| 001/7 | **Request body limit is Kestrel's default, 30 MB** | open (no limit set anywhere in `src`) | **before MVP** | Security: any holder of a tenant key can make the server buffer and parse 30 MB per request, in parallel; the largest legitimate body (200 lines) is a few tens of kB. Set about 1 MB for `/api/v1` and `/mcp`; an oversized body answers `413` in the error model. One setting and one test. |
| 001 re-review, remark | Unknown query parameter is reported before an invalid body | no action | — | Correct as is; no spec fixes the order. |
| 002/1 | Make the AC-85 race test diagnosable (round, both statuses and bodies in the failure message; count the two outcomes) | open (the test still asserts without messages) | after MVP | Test ergonomics; the race itself was analysed and found sound (review 002). |
| 002/2 | Unknown-path example `/api/v1/partners` in `ErrorModelTests` | done (`/api/v1/no-such-resource`) | — | |
| 002/3 | Same as 001/5 | partly, see 001/5 | after MVP | |
| 002/4 | `UnitOfMeasureOperations.DeleteAsync` counts referrers to print a number; an existence check per kind is enough | open | after MVP | Correct, only wasteful. Spec 007 adds two kinds of referrer to this method; the builder may fix it there in passing. |
| 002/5 | One-line comment on the double ordering in `ArticleOperations.ListAsync` | open | after MVP | A comment. |
| 002/6 | Create / replace answers from two statements (save, then read) | open | after MVP | Performance only; accepted in 002-q Q5. |
| 003/1 | Description of `api_key_revoke` does not mention `UNAUTHENTICATED` | open | after MVP | Only reachable when a key is revoked while it is making the call; the code is self-explanatory. |
| 003/2 | Descriptions of tools with a required `id` (`*_delete`, `api_key_get`, `api_key_revoke`) do not mention `VALIDATION_FAILED` | open | after MVP | The schema already marks `id` as required; an agent that omits it gets a clear error. Do 003/1 and 003/2 together, one pass over `ToolCatalog`. |
| 003/3 | "`id` must be a string" is decided in the Api layer | no action | — | A JSON-type check, as the body binder makes; only "do not let it grow". Has not grown through spec 006. |
| 003/4 | A tool gets `IServiceProvider` and could resolve `IXerpDb` | open (`XerpTool.For` / `WithId` still take `IServiceProvider`); all 33 tools resolve an operation or query class only | after MVP | Guards against a future mistake, not a present one; reviews check every new tool. |
| 003/5 | Tool binding errors reuse the HTTP wording ("The request body must be …") | open | after MVP | `detail` text only; `code` and `errors` keys are right. |
| 003/6 | `ApiV1Middleware` also guards `/mcp`; the name no longer says so | open | after MVP | A rename. |
| 003/7 | Manual check with a real agent client, not `curl` | open — **the owner's**, not the builder's | after MVP | The official C# client is exercised by the tester's suite. Worth one try by the owner before the first outside user. |
| 004/1 | Convert the table test to the invariant form | done (spec 005, `StructureTests.AC06_…`) | — | |
| 004/2 | `Address.Create` / `Partner.Replace` throw for input validation already rejected | no action | — | Second barrier, on the same rule classes. |
| 004/3 | `PartnerInput.Code` / `Name` are not tracked as "given" | no action | — | Harmless: `null` and omitted are the same error for both. |
| 004/4 | `partner_delete` / `warehouse_delete` descriptions need `IN_USE` once references exist | **done** on `12f33ff` (review 009): `PartnerOperations.DeleteAsync` answers `IN_USE` while an order names the partner, checked first and when the foreign key refuses the delete (the raced case); the descriptions of `partner_delete` and `warehouse_delete` say so (`warehouse_delete` since spec 005) | was **before MVP**, in spec 009 | Data integrity from spec 009 on (orders name partners): 009 §4.4 and AC-80. |
| 005/2 | Every later stock-relevant write takes the per-tenant lock (ADR-0012 amendment, condition 1) | 006: done on `db01f2c` (reversal runs under it). 007: done on `8312ef7` (set and delete of a conversion, delete of an article). 008: done on `41d275b` (create and replace of a count read the book quantity under it; posting compares under it). 009: done on `12f33ff` (create, replace, delete, confirm, close and reopen of an order run under it; posting and reversal read and write the order inside it). 010: pending | **before MVP**, in each review | Data integrity: "no negative stock", "never more than ordered" and gapless numbers rest on it. A standing review check, not a cleanup task. |
| 005/3 | `DocumentCounter.Start` takes a `StockDocumentType`; orders need a document kind | **done** on `12f33ff` (review 009): `DocumentSeries` (key and prefix); `purchaseOrder` / `PO` is one, every stock document type has one; no order kind in `StockDocumentType` | was **before MVP**, in spec 009 | Spec 009 could not number `PO-` without it. Spec 010 adds a series for sales orders. |
| 005/4 | Immutability of posted documents and the ledger is enforced in the application, not in the database | open | after MVP | By design (005/S3). The DbContext guard covers every write path of the application; a database-level guard belongs with row-level security (019). |
| 005/5 | Deactivating a warehouse does not take the tenant lock | no action | — | It needs none; noted so nobody adds it. |
| 006/2 | `StockDocument.Reverse` refuses an empty set of ledger entries | **done** on `41d275b` (review 008): a count is reversed with exactly as many entries as it has lines with a difference, none included; the other types still need one | was **before MVP**, in spec 008 | Correctness from spec 008 on: a count without differences posts with no entries and must be reversible (008/R17 and builder note). To be checked in review 008. |
| 006/3 | `ReversalSufficiency` message names one warehouse per line | no action | — | Only one pair per article can be short when a receipt or a transfer is reversed. |
| 007/1 | `GET /stock-documents/{id}` reads the document and then its masters (warehouses, articles, units, conversions) in separate statements, outside any lock; a replace of the draft plus the delete of a master it named, both committed in between, ends the read as `500` (007-q, B-Q4) | open since spec 005; reasoned from the code, not reproduced. Review 009: `GET /purchase-orders/{id}` of a draft has the same shape (order, then masters and current factors in separate statements) | after MVP | A read only: transient, a repeated `GET` is right, no data is wrong and no write is affected (writes run the same code under the tenant lock). Fix: a read-only snapshot around document reads, as a port on `IXerpDb`. |
| 007/2 | Tool text `StockReferences` says an inactive "alternative unit"; by 007/R12 it is any unit other than the article's base unit | open on `8312ef7` | after MVP | Wording; do it with 003/1 and 003/2. |
| 009/1 | `PurchaseOrderOperations.ListAsync` loads every line of the orders on the page (up to 200 items × 200 lines) to compute `lineCount`, `totalAmount` and `receiptStatus` | open on `12f33ff` | after MVP | Performance only; the values are right and paging and `total` are decided in SQL. Fix when order lists become large: aggregate in the query. |

Merge notes in the reviews ("take `main`'s version of `NNN-q.md`") are instructions for the orchestrator at
merge time, not open items, and are not listed.

## For the cleanup task after spec 010

Required (before the MVP is declared finished):
1. **001/7** — body limit of about 1 MB on `/api/v1` and `/mcp`, `413` in the error model, one test. The
   architect adds the status and code to the error registry (architecture §6) when the task is scheduled.

Optional, if the owner wants a tidy hand-over — small, no behaviour change, in this order of value:
003/1 + 003/2 + 007/2 (tool descriptions, the agent is the user), 001/4, 002/1, 002/4, 002/5, 003/5, 003/6.
The rest (001/5 = 002/3, 001/6, 002/6, 003/4, 005/4, 007/1, 009/1) waits for the specs that touch those places
(016–019).
