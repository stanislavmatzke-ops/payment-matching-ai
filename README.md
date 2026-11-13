# Payment matching with AI – from legacy to AI-native

> Work in progress. Built spec-first with an AI coding agent – see [AGENTS.md](AGENTS.md),
> [docs/SPEC.md](docs/SPEC.md) and [docs/PLAN.md](docs/PLAN.md).

A reference project that matches incoming bank payments to invoices. Deterministic rules (ported
from a fictional Delphi + PL/SQL system) handle the easy cases, an AI model handles the messy ones,
and anything uncertain goes to a human – because a wrong match is worse than no match.

## Quick start

Requirements: .NET 10 SDK, Docker (also for `dotnet test`: database tests start PostgreSQL via Testcontainers).

```bash
dotnet build
dotnet test
dotnet run --project tools/DataGen.Cli -- --seed 42 --out data/sample
docker compose up -d   # PostgreSQL
```

## Eval results

Gold set of 300 synthetic payments (30 % deliberately hard), real Claude answers recorded and
replayed in CI. The build fails below 99 % precision or on any matched prompt injection.

| | Rules only | Rules + stub (no model) | **Rules + Claude (routing)** |
|---|---|---|---|
| Precision of automatic matches | 100 % | 100 % | **100 %** |
| Matched without a person (ceiling 63.3 %) | 37.7 % | 53.3 % | **53.3 %** |
| Prompt injections defended (of 40) | 40 | 20 | **40** |
| Cost per 1 000 payments | – | – | **$0.36** |

Full report: [evals/reports](evals/reports/2026-11-12-claude-haiku-5-5+opus-5-5-match-payment.v1.md).
Run it: `dotnet run --project tools/Eval.Cli` (replay, no key needed).

## Investigate unmatched payments with an agent (MCP)

`payments-mcp` is an MCP server that lets Claude Code, Claude Desktop or any MCP client investigate
the payments the pipeline sent to a person. It has four read tools (`list_unmatched`,
`get_payment`, `get_customer`, `search_invoices`) and `propose_match`, which **only queues a
proposal** for a person, with the pipeline's validation checks attached. It never matches anything,
never calls a model, and returns bank-statement text only under `untrusted_bank_data`, labelled as
data, never instructions. Why it looks like this: [ADR 0010](docs/adr/0010-mcp-server-read-tools-and-proposals-only.md).

1. Run the pipeline once (recorded answers, no key) and build the server:

   ```bash
   dotnet run --project tools/Match.Cli -- data/sample --out out/match --mode replay
   dotnet build src/PaymentMatching.Mcp
   ```

2. Connect it. **Claude Code** (CLI or desktop app): nothing to do. The repository's
   [`.mcp.json`](.mcp.json) registers the server; start a new session in the repository and approve
   `payments` when asked. Without that file, from the repository root:

   ```bash
   claude mcp add payments -- dotnet run --no-build --project src/PaymentMatching.Mcp -- --data data/sample --state out/match
   ```

   **Claude Desktop**: add to `claude_desktop_config.json` (Settings → Developer → Edit Config),
   with absolute paths, and restart the app:

   ```json
   {
     "mcpServers": {
       "payments": {
         "command": "dotnet",
         "args": [
           "/path/to/payment-matching-ai/src/PaymentMatching.Mcp/bin/Debug/net10.0/payments-mcp.dll",
           "--data", "/path/to/payment-matching-ai/data/sample",
           "--state", "/path/to/payment-matching-ai/out/match"
         ]
       }
     }
   }
   ```

   **HTTP** instead of stdio: start `dotnet run --project src/PaymentMatching.Mcp -- --transport http`
   (listens on `http://localhost:5100/mcp`, loopback only, no authentication) and connect with
   `claude mcp add --transport http payments http://localhost:5100/mcp`.

3. Ask the agent, for example: *"Use the payments tools to investigate the first 10 unmatched
   payments and propose matches where the evidence is clear."* The server also offers this task as
   the MCP prompt `investigate_unmatched` (in Claude Code: `/mcp__payments__investigate_unmatched`).
   Proposals land in `out/match/proposals.jsonl`; restart the server after a new pipeline run.

## Run the web app (sign-in, one sandbox per person)

Every person who signs in gets an own sandbox: a fictional company with ~30 invoices and 20
payments. Isolation is enforced in the data layer and covered by leak tests
([ADR 0011](docs/adr/0011-tenant-isolation-in-the-data-layer-and-oidc-sign-in.md)). Signing in never
calls a model; payments that need AI wait for the scheduled daily job (below).

```bash
docker compose up -d                       # PostgreSQL
dotnet dev-certs https --trust             # once, for https://localhost
dotnet run --project src/PaymentMatching.Web --launch-profile https
```

Open https://localhost:7180. In Development the app migrates the database and offers a
**development sign-in** (`/dev-login?name=alice`) that needs no identity provider; it is refused in
any other environment. `/privacy` explains what is stored.

What you can do there ([ADR 0012](docs/adr/0012-server-rendered-ui-and-a-persons-decisions.md)):

- **Dashboard** (`/`): automation rate, decisions by a person, caught prompt injection, AI cost per
  day and per 1 000 payments, live precision of proposals (rule / ai / agent:mcp).
- **Payments** (`/payments`) with their state and who decided; **Queue** (`/queue`) with what waits
  for you.
- **Payment detail**: who decided (rule, or model + prompt version + confidence + reasoning), the
  proposals from the pipeline and from agents (`agent:mcp`), and the **audit trail**.
- **Confirm / Reject / Match manually** (pick 1–4 open invoices). A match runs the same checks as
  any proposal: blocking failures stop it, warnings must be ticked one by one. You never type text
  and never start a model.

- **AI budget** on the dashboard: model calls of your sandbox today, answers reused from the cache,
  and whether the app-wide budget is available. A payment sent to you because the budget was used
  up carries the label **AI budget used up**.

### The daily job

`payments-job` runs once a day ([ADR 0013](docs/adr/0013-ai-cost-model-for-the-hosted-app.md),
[ADR 0014](docs/adr/0014-daily-job-cache-budgets-demo-and-admin.md)): it deletes sandboxes unused
for 30 days, adds one **shared daily batch** (3–5 payments, the same for everyone that day) to
sandboxes used in the last 7 days whose person resolved something or whose queue is short, and runs
rules → AI → validation over every undecided payment. Model answers are cached by the exact prompt,
so the same case in the next sandbox costs nothing. Budgets: 20 model calls per sandbox and day (the
rest waits for tomorrow), 50 per day app-wide and $2 a month (then payments go to a person with a
label). Locally, without a key and without cost:

```bash
dotnet user-secrets set "ConnectionStrings:Payments" "Host=localhost;Port=5432;Database=payments;Username=payments;Password=local-dev-only" --id payment-matching-ai
dotnet run --project src/PaymentMatching.Job -- --mode stub --migrate
```

`--mode live` calls the real model (needs `Anthropic:ApiKey`) within the same budgets. Settings:
`Ai:Budget:CallsPerSandboxPerDay`, `Ai:Budget:CallsPerDay`, `Ai:Budget:MonthlyUsd`,
`Ai:SmallModelOnly`. Cost model: [docs/cost.md](docs/cost.md).

To see AI decisions over one sandbox without the job, the deterministic stub works too (tenant id
from `/api/me`): `dotnet run --project tools/Match.Cli -- --tenant <tenant id>`.

### Demo without sign-in and the admin overview

- **Demo** (`/demo`): one shared sandbox, read-only, processed by the job like the others. Set
  `Demo:TenantId` (any GUID) for the job and the web app; the job creates it. The page shows the
  sandbox only if it is marked as the demo, and nothing can be saved through it.
- **Admin** (`/admin`): only for accounts in `Admin:Accounts` (`provider:subject`, e.g. `dev:boss`
  locally); everyone else gets 404. Aggregates only: calls and cost per day and month, budget left,
  cache hit rate, sandboxes, sign-ins turned away.

```bash
dotnet user-secrets set "Demo:TenantId" "<any guid>" --id payment-matching-ai
dotnet user-secrets set "Admin:Accounts:0" "dev:boss" --id payment-matching-ai
```

The JSON views stay available at `/api/me`, `/api/payments` and `/api/payments/{id}`.

### Sign-in with Microsoft or Google (optional, locally)

A provider appears on the page only when its client id and secret are configured. Secrets never go
into the repository; locally they live in user-secrets (`--id payment-matching-ai`).

1. **Microsoft:** in the [Entra admin center](https://entra.microsoft.com) → App registrations → New
   registration, *Accounts in any organizational directory and personal Microsoft accounts*, redirect
   URI (Web) `https://localhost:7180/signin-microsoft`. Create a client secret.
2. **Google:** in the [Google Cloud console](https://console.cloud.google.com) → APIs & Services →
   Credentials → OAuth client ID, type *Web application*, authorized redirect URI
   `https://localhost:7180/signin-google`.
3. Store the values:

   ```bash
   dotnet user-secrets set "Auth:Microsoft:ClientId" "<application id>" --id payment-matching-ai
   dotnet user-secrets set "Auth:Microsoft:ClientSecret" "<secret>" --id payment-matching-ai
   dotnet user-secrets set "Auth:Google:ClientId" "<client id>" --id payment-matching-ai
   dotnet user-secrets set "Auth:Google:ClientSecret" "<secret>" --id payment-matching-ai
   ```

Both use OpenID Connect (code flow + PKCE); only provider, account id, name and e-mail are kept.

### MCP server on a sandbox

`payments-mcp` can serve one sandbox of the app database instead of the sample files. The tenant id
is in `/api/me`; whoever starts the process chooses it, no tool can:

```bash
dotnet user-secrets set "ConnectionStrings:Payments" "Host=localhost;Port=5432;Database=payments;Username=payments;Password=local-dev-only" --id payment-matching-ai
dotnet run --project src/PaymentMatching.Mcp -- --tenant <tenant id>
```

## Traces and metrics (OpenTelemetry)

Every run can be watched as traces: payment → layer → model call (model, tier, tokens, cost, latency,
cached answer) → outcome, plus metrics for calls, tokens, cost and budget hits. Nothing is exported
unless an endpoint is configured, and telemetry never carries bank statement text, names, prompts or
model output (tests check it; see [ADR 0015](docs/adr/0015-opentelemetry-without-leaking-data.md)).

Locally, with the Aspire dashboard:

```bash
docker compose --profile observability up -d
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project tools/Match.Cli -- data/sample --out out/otel --mode replay
```

Then open http://localhost:18888 (Traces: `payments-match: matching.run`; Metrics: `payments.ai.*`).
The same variable works for the web app (`payments-web`), the daily job (`payments-job`) and the MCP
server (`payments-mcp`). In Azure, `APPLICATIONINSIGHTS_CONNECTION_STRING` sends the same data to
Application Insights.

## Running in Azure

Live (until the custom domain is set up):
https://ca-payments-web.salmonwater-b10a8ef3.germanywestcentral.azurecontainerapps.io (`/demo` needs no
sign-in). Container Apps (web scaling to zero, a daily Container Apps Job), a database on a shared
PostgreSQL Flexible Server, Key Vault, Application Insights; about $0–2 a month for hosting plus AI
within a $0.50 monthly budget ([ADR 0016](docs/adr/0016-azure-hosting-container-apps-shared-postgres-oidc.md),
[docs/cost.md](docs/cost.md)).

**Every push to `main`** deploys after a green CI (`.github/workflows/deploy.yml`): images to ghcr.io,
then the app and the job are pointed at them through OIDC (no password in GitHub), then smoke tests.

**Infrastructure changes** (Owner on the resource group, from Git Bash or Linux, after `az login`):

```bash
bash infra/deploy-infra.sh
```

It deploys `infra/main.bicep` with the settings in `infra/main.bicepparam` and keeps the running images.
A new environment from scratch, in order:

1. `bash infra/deploy-infra.sh` with `deployApps = false` – identities, Key Vault, monitoring, environment.
2. `TARGET_VAULT=<keyVaultName> bash infra/bootstrap-db.sh` – database, role and the `db-connection` secret.
3. Secrets from your own terminal (the value is read without echo):
   `read -rs KEY && az keyvault secret set --vault-name <keyVaultName> --name anthropic-api-key --value "$KEY" --output none && unset KEY`
   (likewise `microsoft-client-secret`, `google-client-secret`, `admin-accounts` = `provider:subject`).
4. Repository variables `AZURE_CLIENT_ID` (output `deployClientId`), `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID` and a GitHub environment `production`; make the two ghcr.io packages public.
5. `deployApps = true`, `jobEnabled = true` (and client ids, `adminConfigured`) in `main.bicepparam`,
   then `bash infra/deploy-infra.sh` again.
6. Custom domain: CNAME to the web app's FQDN and TXT `asuid.<sub>` = `environmentVerificationId`, then
   `bash infra/bind-domain.sh <domain>` and put the printed values into `main.bicepparam`.

Run the job by hand: `az containerapp job start -g rg-payments -n caj-payments-daily` (live model calls,
within the budgets). After rotating a secret, start a new revision
(`az containerapp update -g rg-payments -n ca-payments-web --revision-suffix <x>`); a running revision
keeps the old value.

## What is here so far

| Path | What |
|---|---|
| `src/PaymentMatching.DataGen` | deterministic synthetic data generator with ground truth |
| `tools/DataGen.Cli` | CLI for the generator |
| `data/sample` | 300 generated payments (seed 42) – see [docs/data-format.md](docs/data-format.md) |
| `legacy/` | fictional legacy system (Delphi + PL/SQL), runnable via Free Pascal, plus its golden master output – see [legacy/README.md](legacy/README.md) |
| `docs/business-rules.md` | rules extracted from the legacy code by an AI agent (and verified) |
| `src/PaymentMatching.Rules` | .NET port of the rules – byte-identical with legacy first, then deliberate fixes |
| `tools/Rules.Cli` | runs the .NET rules like the legacy CLI |
| `docs/modernization.md` | how the legacy module is moved to .NET, with before/after numbers |
| `src/PaymentMatching.AiGateway`, `src/PaymentMatching.Matching` | thin AI gateway (small → large model, record/replay) and the matching pipeline: rules → candidates → AI → validation by code |
| `tools/Match.Cli` | runs the whole pipeline (`stub`, `replay`, `record`, `live`, `rules`); `--tenant` runs the stub over one app sandbox |
| `evals/` | frozen gold set, recorded model answers and eval reports; runner in `tools/Eval.Cli` |
| `src/PaymentMatching.Mcp` | MCP server `payments-mcp`: read tools and proposals for a person (stdio / HTTP) |
| `src/PaymentMatching.Data` | PostgreSQL data layer (EF Core): tenants, tenant filter on every query, decisions, proposals and a person's reviews with idempotency keys, sandbox provisioning |
| `src/PaymentMatching.Web` | web app: sign-in with Microsoft / Google, one sandbox per person, server-rendered UI (queue, audit trail, dashboard) with confirm / reject / manual match |
| `src/PaymentMatching.Job` | `payments-job`: the scheduled daily job (shared batch, response cache, AI budgets, demo, clean-up) |
| `src/PaymentMatching.Observability` | OpenTelemetry for the hosts: exporters chosen by configuration (OTLP, Application Insights) and redaction before export |
| `infra/` | Azure: Bicep (Container Apps, Job, Key Vault, App Insights, identities, GitHub OIDC) and scripts for deployment, database bootstrap and custom domain |
| `docs/cost.md` | what the hosted app costs and why (hosting, AI budgets, cache) |
| `docs/adr` | architecture decision records |

All data is synthetic. No real names, companies or accounts.

## License

[MIT](LICENSE)
