# ADR 0014: The daily job – shared batch, response cache, budgets, demo and admin

- Status: Accepted
- Date: 2026-11-12

## Context

ADR 0013 decided *what* the hosted app spends on AI: refill only where people work, one shared
daily batch, answers cached by prompt, three budget layers, a read-only demo and an admin overview
with aggregates only. Phase 9 builds it. Constraints stay: a visitor never starts a model, CI runs
without a key, tenant isolation lives in the data layer (ADR 0011), a wrong match is worse than none.

## Decision

**A console job, one run then exit (`src/PaymentMatching.Job`, `payments-job`).** Configuration only
from the environment or user-secrets, exit code 0/1. In Azure it becomes a Container Apps Job on a
cron schedule with parallelism 1 (phase 11, `dotnet publish /t:PublishContainer`; no Dockerfile now).
`--mode stub` (default: no key, no cost) or `--mode live`. There is no record/replay mode over
sandboxes; tests use scripted providers. One run:

1. `TenantAdmin.DeleteInactiveAsync` – sandboxes unused for 30 days, never the demo.
2. The demo sandbox is created if missing (id from `Demo:TenantId`, fixed seed, no user).
3. `TenantAdmin.ActiveTenantIdsAsync` – ids of sandboxes used in the last 7 days plus the demo.
4. Per sandbox, in its own tenant context: add today's batch if it has not got it yet and (demo, or
   a person's decision since the last batch, or fewer than 3 payments in the queue); then the normal
   pipeline over every undecided payment with the sandbox's budget.

Running twice a day adds nothing and asks no model twice: the batch date is stored on the tenant and
decisions are idempotent (ADR 0006).

**The shared batch** is generated from the date: 3–5 payments, 1–2 of the kinds that reach the AI
layer (unreadable invoice number, VS typo, several invoices, an injection attempt), the rest exact
matches or third-party payers that the rules settle. Which kinds reach the AI layer was measured
over 400 days, not assumed: partial payments and a VS in the message sometimes do, so they are not
in the batch. The generator got options for id prefixes and the first invoice number (defaults keep
`data/sample` byte-identical); batch ids carry the date (`P261112-0001`, `C261112-001`,
`2026-13xxxx`), so the prompt for a batch payment is the same in every sandbox.

**The response cache is a provider decorator (`CachingProvider`)** keyed by the record/replay key of
ADR 0007 plus a source (`live` / `stub`, so a stub answer never serves a live run). A hit returns
the stored answer marked `Cached`; the gateway counts it at $0 and **outside its call budget**, and
may use it even when the budget is used up (`IAnswerCache`). The answer is parsed and validated by
code in every sandbox. Cached calls are visible in the audit trail.

**Global tables live in a second context (`OperationsDbContext`)** with its own migrations history:
`ai_response_cache` and `daily_usage` (calls, cost, cache hits, sandboxes refilled / deleted,
sign-ins turned away). They hold no person's data. `PaymentsDbContext` keeps the rule "every entity
is tenant-owned", and its tests did not have to change.

**Three budget layers become one number per sandbox** (`AiAllowance`): the gateway's `MaxCalls` =
min(2 − the sandbox's model calls today [from its audit records], 50 − the app's calls today
[ledger], the month's remaining dollars / $0.05 reserve per call). The ledger is updated with atomic
`x = x + n` after each sandbox, before saving its decisions (the calls happened either way).
When the tightest layer is the sandbox's, AI payments beyond it stay undecided for the next run
(`DeferAiWhenBudgetExhausted`); when it is the app's day or month budget, they go to a person with
the code-only flag `ai_budget_used_up` (not in the model's answer schema). `Ai:SmallModelOnly`
removes the large model from the routes.

**The two exceptions to "the tenant comes only from the cookie" are GET only:**

- `/demo`: the tenant id comes from server configuration, the tenant must be marked `IsDemo`
  (configuration pointing at a person's sandbox shows nothing), and the context is read-only:
  `SaveChanges` throws. The demo has no user, so no sign-in leads into it. Rate limited per address.
- `/admin`: only accounts in `Admin:Accounts` (`provider:subject`); everyone else gets 404. It reads
  `TenantAdmin.SummaryAsync` (counts only) and the ledger. A test fixes that `TenantAdmin` returns
  only ids and counts.

## Consequences

- AI cost no longer grows with the number of users: each day's batch is answered once (1–2 calls),
  every other sandbox reuses the answers. New sandboxes still have their own starter data (~5–7 AI
  payments), at most 2 calls a day each, so their AI payments arrive over a few days.
- Everyone sees the same new payments on a day; the demo grows by 3–5 payments a day.
- Two job instances at once could overshoot a budget by a call or two; the Container Apps Job runs
  with parallelism 1 and the Anthropic console spend limit stays the hard backstop.
- No new packages.
