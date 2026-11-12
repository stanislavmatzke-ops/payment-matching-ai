# ADR 0013: AI cost model for the hosted app (demand-driven daily batch, response cache, budgets)

- Status: Accepted (per-sandbox limit raised from 2 to 20 calls a day, see ADR 0014)
- Date: 2026-11-12

## Context

SPEC 9b first planned a scheduled job that adds 5–10 payments to every active sandbox every day and
runs the AI layer over them, under a global cap of about 500 model calls a day. The app is a public
reference: the AI in it must be real (a model decides, its answer is validated, audited and priced),
but it should not spend tokens on data nobody looks at.

Measured cost (phase 5 evals, `routing` on the gold set): $0.107 for 136 payments that needed the
model, i.e. about **$0.0008 per AI payment**; 25–40 % of generated payments need the model, the rest
the rules settle for free. The original plan would cost about $1.5 a month with 20 active
sandboxes and grow with every user up to the cap (~$12 a month). Cheap, but it grows with traffic
rather than with use, and most of it would process payments no one ever opens.

## Decision

1. **Refill on demand.** The daily job only touches sandboxes used in the last 7 days (plus the
   demo), and only adds payments when the person resolved something since the last run or fewer
   than ~3 items wait in the queue. An idle sandbox gets nothing and costs nothing.
2. **Mostly deterministic, a little AI.** A daily batch has 3–5 payments; most of them are cases the
   rules settle, 1–2 are the ones a model is worth asking about (unreadable invoice number, third
   party, an occasional prompt injection attempt).
3. **One shared daily batch, one answer per prompt.** All sandboxes get the same new invoices and
   payments that day (seed from the date). Model answers are cached under the fingerprint of the
   exact prompt (the record/replay key from ADR 0007), so the second and every further sandbox with
   the same case gets the stored answer without a call. The answer is still validated by code in
   every sandbox; the cache saves the call, not the checks. When a sandbox's state differs (a person
   matched an invoice by hand), the prompt differs and the model is called, within the budgets.
   Cost no longer depends on the number of users.
4. **Three budget layers.** At most 2 model calls per sandbox per day, 50 per day in total, and a
   monthly budget in dollars (default $2, configurable). When a budget is used up, payments go to a
   person with a visible "AI budget used up" label. A spend limit in the Anthropic console is the
   hard backstop outside the app.
5. **Large model only when it can change the outcome** (ADR 0009), and a setting to run the hosted
   app on the small model only (evals: same precision, automation 52.0 % instead of 53.3 %, about
   half the cost).

Also decided with it (SPEC 9b):

- **Read-only demo without sign-in:** one shared sandbox processed by the job like the others; no
  actions; to act, a visitor signs in and gets an own sandbox.
- **Admin overview (`/admin`):** accounts listed in configuration only; aggregates across sandboxes
  (calls and cost per day and month, budget left, cache hit rate, active sandboxes, new per day,
  waiting list) through `TenantAdmin`, never another person's payments.
- Visitors still never start a model and never send text to one.

## Consequences

- Expected AI cost of the public app: a few model calls a day, tens of cents a month, never above
  the monthly budget. The database, not the model, becomes the main running cost.
- Everyone sees the same new payments on a given day. For a demo of the workflow that is fine; the
  starter data stays per sandbox.
- The response cache is production code now, not only a test tool; it must key on the exact prompt
  and model settings, as the replay key already does, and its hits are shown on `/admin`.
- The demo and `/admin` add two deliberate exceptions to "the tenant comes only from the cookie":
  the demo tenant comes from server configuration and is read-only, and `/admin` reads aggregates
  only. Both get their own tests in phase 9.
