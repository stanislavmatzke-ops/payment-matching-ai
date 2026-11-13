# ADR 0017: Instant AI decisions for new sandboxes – a weekly starter set and the response cache

- Status: Accepted
- Date: 2026-11-13
- Amends: ADR 0011 (starter data), ADR 0014 (daily job)

## Context

Since phase 7 a first sign-in creates a sandbox with starter data whose seed came from the tenant id,
and runs only the rules layer: a visitor must never start a model (SPEC 9b). The AI payments then waited
for the next daily job (05:00 UTC). In production the developer signed in and saw "waiting for AI" until
the next morning: the part of the app that is the point of it was missing from the first impression.

Calling the model at sign-in would break the hard rule and make cost grow with sign-ins. Running the
job more often only shortens the wait and still pays for every new sandbox.

## Decision

**All sandboxes created in the same week start from the same starter set.** The seed is the date of
that week's Monday (UTC, `StarterSet.SeedOf`), the generator's reference date is that Monday. Each
sandbox still gets its own copy of every row, keyed by its tenant id: what one person confirms, rejects
or matches changes only their rows (covered by a test that decides a payment in one sandbox and checks
the same payment in another).

**The daily job decides the week's starter set first**, in memory (`DecisionLog`), with the model behind
the response cache and within the app's day and month budgets. The first run of a week asks the model
(about 5–8 calls); every later run finds all answers cached and costs nothing.

**A sign-in takes the AI decisions from the cache only.** `SandboxProvisioner.DecideStarterAsync` runs
the same two passes as the precomputation – rules over all payments, then the AI layer over the rest –
so the prompts and therefore the cache keys are identical. At sign-in the gateway has a budget of zero
calls; the cache is consulted before the budget (ADR 0014), so cached answers are used and anything
else waits for the job. Behind the cache sits a provider that throws: a sign-in cannot reach a model
even by mistake. The source is configured (`Sandboxes:InstantAiSource`, default `live`, `off` disables),
so a stub answer never serves a live sandbox. Every answer is still validated by code in each sandbox.

The demo keeps its own fixed seed.

## Consequences

- A new person sees rule and AI decisions immediately after signing in, at no cost per sign-in.
- AI cost no longer depends on new sandboxes at all: about 5–8 calls a week for the starter set plus the
  shared daily batch (1–2 calls a day). Manual matches can still make a later prompt differ.
- Everyone who signs in during a week sees the same starter data, as everyone already sees the same daily
  batch. The data stays synthetic and per sandbox.
- If the job has not run yet in a new week (Monday before 05:00 UTC), new sandboxes wait for it as before.
- Tests: the precomputation fills the cache and two sign-ins decide all AI payments from it with no model
  request; without a cached answer nothing is called and payments wait; a stub cache never serves `live`;
  the week boundary.
