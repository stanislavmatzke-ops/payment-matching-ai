# What the hosted app costs

This page covers the AI part, which phase 9 built. Hosting (Container Apps, PostgreSQL, Key Vault)
is estimated in phase 11. Decisions: [ADR 0013](adr/0013-ai-cost-model-for-the-hosted-app.md),
[ADR 0014](adr/0014-daily-job-cache-budgets-demo-and-admin.md).

## Measured price of one AI payment

From the evals (`routing`, gold set, real model, prices in `PriceTable` as of 2026-11-03):
$0.107 for 136 payments that needed the model, i.e. **about $0.0008 per AI payment** (a small-model
call ~$0.0002, an escalation to the large model ~$0.01 and rare: 4 of 136). Small model only:
about half of that, same precision, automation 52.0 % instead of 53.3 %.

## What the daily job spends

| Source | Calls per day | Notes |
|---|---|---|
| Shared daily batch | 1–2 | answered once, every other sandbox reuses the cached answer ($0) |
| Starter data of new sandboxes | up to 2 per new sandbox | own data per sandbox, ~5–7 AI payments spread over a few days |
| Sandbox where a person matched by hand | occasionally | the prompt differs, so the model is asked again |

Example: 20 active sandboxes, 2 new ones a day → about 1–2 + 4 = **~6 calls a day, ~$0.005 a day,
~$0.15 a month**. Without the cache and demand-driven refill (the original plan) the same traffic
would be ~$1.5 a month and grow with every user.

## Budgets (configuration `Ai:*`)

| Setting | Default | When used up |
|---|---|---|
| `Ai:Budget:CallsPerSandboxPerDay` | 2 | the sandbox's AI payments wait for the next run |
| `Ai:Budget:CallsPerDay` | 50 | AI payments go to a person, label "AI budget used up" |
| `Ai:Budget:MonthlyUsd` | 2 | same; a call needs `Ai:Budget:ReservePerCallUsd` ($0.05) left |
| `Ai:SmallModelOnly` | false | no escalation to the large model |

Cached answers are not calls: they cost nothing and do not count against any budget. The hard
backstop outside the app is the spend limit in the Anthropic console. The state is on the
dashboard (per sandbox) and on `/admin` (calls and cost per day and month, budget left, cache hit rate).

## What a visitor can cost

Nothing directly: visitors never start a model. Signing in creates a sandbox (database rows, rules
only); its AI payments are handled by the job within the budgets above. New sandboxes per day are
limited (`Sandboxes:MaxNewPerDay`); people turned away are counted on `/admin`.
