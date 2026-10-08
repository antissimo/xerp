# ADR-0004: Problem Details with stable machine-readable error codes

Status: accepted (2026-10-08)

## Context
Agents recover from errors only if errors are precise and stable. The scaffold already returns
`application/problem+json` with a `code` extension for some errors, but validation errors use the ASP.NET default
shape without a code, and authentication errors do not exist.

## Decision
Every error under `/api/v1` is RFC 9457 `application/problem+json` with `code` (SCREAMING_SNAKE_CASE, stable,
listed in `docs/architecture.md` section 6), `status`, `title` (= code), `detail` (human text, may change) and,
for validation, `errors` (camelCase field -> messages). Errors originate in Application as `AppError`; the Api
maps code -> status in one place; MCP returns the same code in a tool error. Validation is `400`. Records of
other tenants are `404 NOT_FOUND`. Unknown JSON properties are validation errors. Out-of-range paging is rejected,
not clamped.

## Alternatives
- **HTTP status only.** Too coarse: two different 409s need different agent reactions.
- **Custom envelope `{ ok, data, error }` on every response.** Non-standard; breaks HTTP tooling.
- **422 for validation.** Defensible; 400 chosen because it is the ASP.NET Core default and model binding
  failures cannot be cleanly separated from rule violations.
- **Exceptions for business errors.** Simple to write, but hides the set of possible errors from the operation
  signature. Application returns results; exceptions are for bugs.

## Consequences
- Error codes are API surface: renaming one is a breaking change. New codes are added to the registry by spec.
- Slightly more code than framework defaults (custom validation response, auth challenge bodies, 404 fallback).
- Tests assert on `status` + `code` (+ `errors` keys), never on message text.
