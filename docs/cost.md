# What the hosted app costs

Decisions: [ADR 0013](adr/0013-ai-cost-model-for-the-hosted-app.md),
[ADR 0014](adr/0014-daily-job-cache-budgets-demo-and-admin.md),
[ADR 0016](adr/0016-azure-hosting-container-apps-shared-postgres-oidc.md) (hosting).

## Hosting in Azure (estimate, per month)

| Resource | Setting | Cost |
|---|---|---|
| Container Apps environment | Consumption, no VNet | $0 |
| Web app | 0.25 vCPU / 0.5 GiB, one replica always running (`webMinReplicas = 1`; 0 = scale to zero) | ~$3–5 (scale to zero: ~$0–1) |
| Daily job | one run a day, a minute or two | ~$0 |
| PostgreSQL | a database on an existing B1ms server (shared) | $0 extra (an own B1ms server: ~$16–18) |
| Key Vault | Standard, a few thousand reads | ~$0.01 |
| Log Analytics + Application Insights | daily cap 0.1 GB, 30 days | ~$0 (within the free 5 GB) |
| Images | ghcr.io, public packages | $0 (Azure Container Registry Basic: ~$5) |
| Managed certificate for the custom domain | | $0 |
| **Hosting total** | | **~$3–7 (under ~170 CZK); with scale to zero ~$0–2** |

The Container Apps free grant is per subscription; another app in the same subscription may already
use it, so the estimate counts the web app's seconds as paid. AI comes on top, within the budgets below.

The first live run in Azure (2026-11-13) created the demo and decided its starter data and the day's
batch at once: 8 model calls, **$0.031** (the estimate before the run was under $0.01; escalations
to the large model made the difference). Later days need only the batch, 1–2 calls, answered once for
all sandboxes.

## Measured price of one AI payment

From the evals (`routing`, gold set, real model, prices in `PriceTable` as of 2026-11-03):
$0.107 for 136 payments that needed the model, i.e. **about $0.0008 per AI payment** (a small-model
call ~$0.0002, an escalation to the large model ~$0.01 and rare: 4 of 136). Small model only:
about half of that, same precision, automation 52.0 % instead of 53.3 %.

## What the daily job spends

| Source | Calls per day | Notes |
|---|---|---|
| Shared daily batch | 1–2 | answered once, every other sandbox reuses the cached answer ($0) |
| Starter set of the week (40 payments) | ~10–16 per week, once | the same starter data for every sandbox created that week; sign-ins reuse the cached answers ($0, ADR 0017) |
| Sandbox where a person matched by hand | occasionally | the prompt differs, so the model is asked again |

Example: any number of active and new sandboxes → about 1–2 calls a day for the batch plus ~5–8 a
week for the starter set (40 payments), **~$0.10–0.40 a month** depending on escalations (measured in Azure: a starter
set with escalations ~$0.015–0.03). New sign-ins add nothing. Without the cache and demand-driven refill
(the original plan) cost would grow with every user.

## Budgets (configuration `Ai:*`)

| Setting | Default | When used up |
|---|---|---|
| `Ai:Budget:CallsPerSandboxPerDay` | 20 | the sandbox's AI payments wait for the next run |
| `Ai:Budget:CallsPerDay` | 50 | AI payments go to a person, label "AI budget used up" |
| `Ai:Budget:MonthlyUsd` | 2 (production 0.50) | same; a call needs `Ai:Budget:ReservePerCallUsd` ($0.05; production $0.02) left |
| `Ai:SmallModelOnly` | false | no escalation to the large model |

Cached answers are not calls: they cost nothing and do not count against any budget. The hard
backstop outside the app is the spend limit in the Anthropic console. The state is on the
dashboard (per sandbox) and on `/admin` (calls and cost per day and month, budget left, cache hit rate).

## What a visitor can cost

Nothing: visitors never start a model. Signing in creates a sandbox (database rows) from the week's
starter set, runs the rules and takes the AI decisions from the response cache the job filled; anything
not cached waits for the job, within the budgets above. New sandboxes per day are
limited (`Sandboxes:MaxNewPerDay`, default 5, production 30); people turned away are counted on `/admin`. For a demo
with a handful of clients: about $0.05–0.10 a month; production runs with a monthly budget of $0.50
(`Ai:Budget:MonthlyUsd` in `infra/main.bicepparam`) and a $5 spend limit on a separate Anthropic
workspace with its own key.

## Compared with manual work (assumptions, not measurements)

What matters for a company is not the hosting of this demo but the cost per payment in its own
operation. A rough comparison, with every assumption stated:

| | Assumption | Per 1,000 payments |
|---|---|---|
| Matching everything by hand | 1.5 min per payment, CZK 600 per hour fully loaded (~$26) | 25 h, ~$650 |
| Legacy rules + a person for the rest | legacy matches 66 %; a person does the other 34 % at 1.5 min; 19 wrong invoices found later at ~30 min each | ~$470 (8.5 h + 9.5 h) |
| Rules + AI (`routing`, gold set) | 53 % matched automatically with 100 % precision; a person confirms the other 47 % from a prefilled proposal at ~1 min | ~$200 of work (7.8 h) + **$0.36** for the model |

The model's cost is noise next to a person's time: the saving comes from fewer payments for a person
and from the proposal and reasoning the person starts from, and the main benefit is that no payment
is matched to a wrong invoice. The numbers for automation and precision are from the evals
([evals/reports](../evals/reports/2026-11-12-claude-haiku-5-5+opus-5-5-match-payment.v1.md)); the
minutes and the hourly rate are assumptions to replace with a company's own.
