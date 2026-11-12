# ADR 0012: Server-rendered UI and a person's decisions

- Status: Accepted
- Date: 2026-11-12

## Context

Phase 8 turns the hosted app (SPEC 9b) from a read-only API into something a person works in:
payments with their state and who decided, a queue with Confirm / Reject / Match manually, an audit
trail per payment and a dashboard. These are the first writes a visitor can make. Constraints: a
visitor never starts a model and never sends text to one; actions only on existing data; a wrong
match is worse than none; tenant isolation in the data layer (ADR 0011); functionality over design;
no dependency without a reason.

## Decision

**UI technology: Razor components rendered on the server, no JavaScript.** Each page is a Razor
component returned from an explicit minimal API endpoint (`RazorComponentResult`); there is no
`MapRazorComponents`, no interactivity, no SignalR, no WebAssembly. Razor encodes every value it
writes, so bank statement text, model reasoning and agent reasons cannot become markup (no component
uses `MarkupString`). The CSP stays strict: no scripts at all, one same-origin stylesheet
(`style-src 'self'`). Nothing new to install: it is all in the shared framework.
Rejected: a React page (npm toolchain and a build step in CI, a JSON API plus CSRF handling in
JavaScript, more dependencies than the feature needs) and interactive Blazor Server (a websocket
per user, a script in the CSP, server state per circuit, worse for scale-to-zero hosting).

**A person's decision is a row of its own (`reviews`), never a change to the pipeline's record.**
The primary key `(tenant, decision key)` — the statement line's idempotency key — allows one human
decision per payment: a double click or a second tab gets "already decided". The row is never
updated; it keeps the action, the invoices, the proposal, the validation failures at that moment,
the warnings the person accepted, the proposals the person saw and the user. Confirmed and manual
matches also go to `invoice_matches`, whose key `(tenant, invoice)` already guarantees that an
invoice is matched once — by the pipeline or by a person. The pipeline (`DbDecisionStore`) and the
MCP server count a person's matches as taken; the MCP server refuses to propose for a payment a
person has decided.

**Three actions, no free text.** Confirm takes a proposal id (the invoices come from the stored
proposal, never from the request), Reject takes nothing, Match manually takes 1–4 invoice numbers
picked from the list of open invoices. Fields must look like ids or known check codes, otherwise
400; everything is then resolved inside the person's sandbox, so another tenant's payment, proposal
or invoice does not exist. Actions are open only for payments the pipeline sent to a person or did
not match, and only once; payments waiting for the AI run are not.

**The same checks as for any proposal, and a person must see them.** Confirm and manual match run
`ProposalChecks` — the pipeline's `ProposalValidator` over the code's candidates plus the chosen
invoices, now shared with the MCP server. Failures split into two kinds:

- *blocking*, never overridable: an invoice already matched, invoices of different customers, no
  or unknown invoices;
- *warnings*, which are exactly the cases a person exists for (partial payment, third-party payer,
  a reference the payment does not contain, a tie, suspected prompt injection): they are shown with
  an explanation and the person must tick each one. The server accepts the match only when the
  ticked set equals today's failures, so a stale page or a hand-made request cannot skip one.

Manual matching first shows a preview of the checks (GET), then saves (POST).

**Proposals from agents sit next to the pipeline's.** The queue merges the pipeline's proposal and
`agent:mcp` proposals by invoice set (`Proposal.IdFor`), shows who proposed what with confidence,
prompt version and reasoning, and recomputes the checks now.

**CSRF:** every write requires sign-in and an anti-forgery token (`__Host-` cookie, SameSite=Strict,
form field), checked by the framework for form endpoints and by an endpoint filter for the others
(reject, sign-out). Writes are rate limited per person.

**Dashboard and live precision.** Automation rate, decisions by a person, caught prompt injection,
AI cost per day and per 1 000 payments, all from the tenant's own audit records. "Live precision"
counts, for every proposal a person saw when deciding, whether the person matched exactly its
invoices — per proposer kind (rule, ai, agent:mcp). It is feedback from use, not ground truth; the
evals stay on the reviewed gold set.

**Local tryout without a model:** `match --tenant <id>` runs the pipeline with the deterministic stub
over one sandbox, so AI decisions, cost lines and the audit trail can be seen locally. Only the stub
is allowed there; real model runs over sandboxes belong to the scheduled job with its limits.

## Consequences

- Cross-tenant tests cover every action in the data layer and over HTTP with identical ids in two
  sandboxes; the route test now lists the exact write endpoints and the parameter types they may take.
- A person cannot undo a decision in the app. That is deliberate for now (append-only audit); an
  undo would be a new row, not an update.
- Read-only demo without sign-in is moved to phase 9: it is only worth showing with data the daily
  job has processed.
- No new packages.
