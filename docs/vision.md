# Vision

> **STATUS: DRAFT — written by the architect agent; the owner has not reviewed the text as a whole.**
> It is derived from `CLAUDE.md` ("AI is not a feature of the ERP, AI is a user of the ERP") plus the owner's
> decisions of 2026-10-08 listed at the end. Owner: please confirm, correct or replace.

## What we are building

An ERP whose primary users are AI agents, working on behalf of (and supervised by) people.

Classic ERPs are built around screens: the data model and the rules are reachable only through forms designed
for a human clerk. Bolting an assistant on top of that gives an agent a worse version of the human UI.
We invert it: the system's native surface is a set of precise, self-describing operations, and every client —
an MCP agent, a CLI, a web UI — is a thin shell over the same operations. This repository contains the backend
and the MCP server; the CLI and the web UI are separate projects that consume its HTTP API.

## Principles

1. **AI is a user, not a feature.** There is no "AI module" and the ERP never calls an LLM. Agents connect
   from outside (MCP, CLI, HTTP) with their own credentials, exactly like a human user or an integration.
2. **One set of operations.** Anything a human can do through a client of the HTTP API an agent can do through
   MCP, and vice versa. Business rules live once, in the Domain/Application layers. No client contains business logic.
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
- Country-specific localisation (tax rules, tax-id validation, fiscalisation) in the core.
- The CLI and the web UI (separate projects).

## Owner decisions (2026-10-08)

1. **Jurisdiction-neutral first.** No country-specific tax, tax-id validation or fiscalisation in the core
   roadmap; taxes are modelled generically.
2. **Hosted multi-tenant SaaS; one tenant = one legal entity.** Confirmed.
3. **Agents may post documents by default.** The owner answered "yes" to "may agents post documents by default,
   or only prepare drafts for a human?"; this is interpreted as *agents may post by default*, restrictable per
   credential through permissions. Correct this line if the interpretation is wrong.
4. **The CLI and the web UI are separate projects, not part of this repository.** This repository is the
   backend: Domain, Application, Infrastructure, Api and the MCP server. The HTTP API is the contract those
   client projects consume.
5. **A platform admin key in configuration** is accepted as the bootstrap for tenant provisioning.
6. Documentation language: English.
