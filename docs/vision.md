# Vision

> **STATUS: DRAFT — written by the architect agent, not yet confirmed by the project owner.**
> It is derived from `CLAUDE.md` ("AI is not a feature of the ERP, AI is a user of the ERP") and nothing else.
> Owner: please confirm, correct or replace. Points that need an explicit decision are listed at the end.

## What we are building

An ERP whose primary users are AI agents, working on behalf of (and supervised by) people.

Classic ERPs are built around screens: the data model and the rules are reachable only through forms designed
for a human clerk. Bolting an assistant on top of that gives an agent a worse version of the human UI.
We invert it: the system's native surface is a set of precise, self-describing operations, and every client —
the web UI, the CLI, an MCP agent — is a thin shell over the same operations.

## Principles

1. **AI is a user, not a feature.** There is no "AI module" and the ERP never calls an LLM. Agents connect
   from outside (MCP, CLI, HTTP) with their own credentials, exactly like a human user or an integration.
2. **One set of operations.** Anything a human can do in the web UI an agent can do through MCP/CLI, and vice
   versa. Business rules live once, in the Domain/Application layers. No client contains business logic.
3. **Operations are built to be used without guessing.** Explicit inputs, stable machine-readable error codes,
   validation messages that say what to fix, no hidden state that only a screen would reveal.
4. **Every action has an accountable actor.** Each credential is marked as human or agent; every write records
   who did it. An agent's work must be reviewable after the fact.
5. **The books cannot be quietly rewritten.** Draft documents are editable; posted documents and ledger entries
   are immutable and are corrected only by reversing entries. This is what makes it safe to let agents write.
6. **Tenants are isolated by construction.** Every query is tenant-scoped; one tenant can never see or infer
   another tenant's data.
7. **Proven ERP behaviour over invention.** Where Odoo, ERPNext, Business Central and SAP Business One agree
   (ledger-based stock, document flows, number series, reversal-only corrections) we follow them
   (see `docs/roadmap.md`). We innovate on the interface, not on accounting.

## Non-goals (for now)

- Embedding an LLM, chat UI or "copilot" inside the product.
- Manufacturing, payroll, HR, CRM, e-commerce.
- A customisation/plug-in framework.

## Needs an owner decision

1. Is the target market Croatia/EU first (VAT, OIB, fiscalisation, EUR), or jurisdiction-neutral first?
   This decides when tax and legal-numbering work enters the roadmap.
2. Is this a hosted multi-tenant SaaS (assumed, because of the tenant rule) and is one tenant = one legal
   entity (assumed)?
3. How much autonomy do agents get by default: may an agent post documents, or only prepare drafts for a
   human to post? (The roadmap assumes permissions make this configurable per credential.)
4. Is the React web UI a first-class deliverable early, or a later review/approval surface? (The roadmap
   assumes later.)
