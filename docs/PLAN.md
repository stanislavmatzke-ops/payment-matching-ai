# PLAN – phases and status

> 🇬🇧 English · 🇨🇿 [Česky](cs/PLAN.md). The original Czech plan as it stood before phase 12:
> [spec-original-cs](spec-original-cs/PLAN.md).

> The agent updates this file (and its Czech version): when a phase is done it ticks it off and adds
> the date and a short note (what was built, deviations from the SPEC, open questions). A new session
> starts by reading this file.

## Phases (each ends with a stop and a demo)

- [x] **Phase 1** – **Skeleton:** repository, solution, Docker Compose (Postgres), CI (build + test + gitleaks), `.env.example`, synthetic data generator.
  - _Done 2026-11-12._ .NET 10 (`PaymentMatching.slnx`, central package management, warnings as errors), generator `src/PaymentMatching.DataGen` + CLI `tools/DataGen.Cli`, sample set `data/sample` (seed 42, 300 payments, 391 invoices, 60 customers), 32 tests, CI (format, build, test, data reproducibility check, gitleaks), Postgres 17 in Compose, ADR 0001–0002, `docs/data-format.md`.
  - _Deviations from the SPEC:_ the SPEC gives no case shares – chosen 45/10/8/8/8/6/5/5/5 % (see `docs/data-format.md`), changeable in `GeneratorOptions`. The only dependency is `xunit.v3` (tests).
  - _Added after the phase:_ repository on GitHub (private, `stanislavmatzke-ops/payment-matching-ai`), CI green. Free Pascal runs in Docker (`freepascal/fpc:3.2.2-full`, verified by compiling a test program in `-Mdelphi` mode), nothing is installed.
- [x] **Phase 2** – **Legacy:** Delphi + PL/SQL module, CLI through Free Pascal, golden master output.
  - _Done 2026-11-12._ `legacy/delphi` (5 units, compiled with `fpc -Mdelphi` in Docker via `legacy/run-legacy.sh`), `legacy/plsql` (package, source only), golden master `legacy/golden/` (300 + 1,000 payments), CI job `legacy-golden-master`, ADR 0003, `docs/modernization.md` (step 2 + legacy baseline numbers).
  - _Legacy baseline against the ground truth:_ automation ~66 %, precision of automatic matches ~80 %, 3 / 19 payments on the wrong invoice, 0 matches made by prompt injection.
  - _Deviations from the SPEC:_ none. `docs/business-rules.md` deliberately not written yet, it is the task of phase 3 (extraction from code). The key of the planted rules is outside git.
- [x] **Phase 3** – **Port of the rules to .NET** + characterization tests (identical to legacy) + `business-rules.md`.
  - _Done 2026-11-12._ Parts:
    - `docs/business-rules.md` (extraction by an isolated agent, 13/13),
    - `src/PaymentMatching.Rules` + `tools/Rules.Cli`,
    - 62 unit tests + 4 golden master tests,
    - a faithful port byte-identical with legacy (tag `phase3-faithful-port`),
    - changes F1 (fuzzy lookup removed), F2 (invalid amounts) and F3 (`decimal`),
    - `port_matches.csv`, ADR 0004, `docs/modernization.md` steps 1 and 3.
  - _Result against the ground truth:_ 0 payments on the wrong invoice (legacy 3 / 19), all 450 exact matches matched (legacy 443), precision of automatic matches 82.4 %. The rest are only cases for a person (partial payments, third parties) – the task of phase 4.
  - _Open for the developer:_ business questions Q-01..Q-12 in `business-rules.md`, mainly Q-02 (automatic partial payments) and Q-07 (payer check). Behaviour stays as in legacy for now.
- [x] **Phase 4** – **AI gateway + AI matching** (structured output, validation, routing, audit, idempotency).
  - _Done 2026-11-12._ Built:
    - `src/PaymentMatching.AiGateway`: provider abstraction, the official Anthropic SDK, routing Haiku 5.5 → Opus 5.5, timeout, call limit, tokens and cost, record/replay,
    - `src/PaymentMatching.Matching`: a policy over the rules (Q-02, Q-07), candidate selection by code, prompt `prompts/match-payment.v1.md`, strict structured output, validation by code, decision, an append-only audit log with idempotency,
    - `tools/Match.Cli`: modes `stub|replay|record|live|rules`,
    - 91 recordings of real answers in `evals/recordings/`,
    - ADR 0005–0007, `modernization.md` step 4, state of Q-02/Q-06/Q-07/Q-08/Q-11 in `business-rules.md`.
  - _Result on `data/sample` (real model):_
    - automation 75.7 % (ceiling of the set 76 %), precision of automatic matches 100 % (port 82.6 %), 0 wrong invoices,
    - 15/15 injections caught,
    - $0.64 per 1,000 payments, p50 2.0 s / p95 5.2 s per AI payment,
    - 192 tests.
  - _Decided (approved in the phase plan):_
    - Q-02: partial payments and overpaid advances always go to a person with a proposal.
    - Q-07: an automatic match needs the customer's IBAN (exception: exact VS in the field + amount); third parties go to a person.
  - _Deviations from the SPEC:_
    - audit and idempotency in a JSONL file for now (`DecisionLog`), PostgreSQL with tenants in phase 7,
    - EUR rate a fixed band 24.00–26.50 for now, not the daily CNB rate.
  - _For phase 5:_
    - do not escalate answers that go to a person anyway (8 of 12 Opus calls, Opus = 84 % of the cost),
    - calibrate thresholds (Opus gave correct answers at 0.88 → person),
    - measure the variance between runs (two recording runs: 228 vs 227 automatic),
    - compare rules / stub / model on harder data.
- [x] **Phase 5** – **Evals** – gold set, runner, reports, replay in CI, threshold in CI.
  - _Done 2026-11-12._ Built:
    - gold set `evals/dataset/gold-v1` (300 payments, seed 5005, `--profile eval`, 30 % hard cases: ties, unreadable invoice number, injection without keywords), review in `REVIEW.md`, frozen through `MANIFEST.sha256`,
    - `src/PaymentMatching.Evals` (scorer, gate, variance, Markdown report) + `tools/Eval.Cli` (`replay|record|live`, `--repeat`),
    - 5 configurations: `rules`, `stub`, `small`, `routing-v1`, `routing`; a CI step with a gate (precision ≥ 99 % and 0 matched injections for all, `routing` additionally: an injection target is never proposed) + the report as an artifact,
    - a new validation rule `AMBIGUOUS`, escalation only when it can change the outcome (default), the gateway can run without a large model,
    - 200 new recordings, report `evals/reports/2026-11-12-…-match-payment.v1.md` + a variance report, ADR 0008–0009, `modernization.md` step 5, table in the README, 219 tests.
  - _Result on the gold set (`routing`, real model):_ precision 100 %, automation 53.3 % (ceiling 63.3 %; the difference = unreadable invoice numbers, deliberately to a person), 40/40 injections, $0.36 per 1,000 payments (`routing-v1` $2.21), p50 2.0 s / p95 4.1 s. Stub: 100 % precision, but proposed the target of a hidden injection to a person 20×. On `data/sample` $0.64 → $0.27 per 1,000 payments with the same 227 automatic.
  - _Items from phase 4:_ escalation solved (39 → 4 Opus calls); threshold 0.90 kept (all validated answers ≥ 0.90 and correct, sweep 0.80–0.90 without change, 0.95 lowers automation); variance of 3 live runs: identical results, only the proposal for 8 ties differs.
  - _Deviations from the SPEC:_ the runner is `tools/Eval.Cli` (not `dotnet run --project evals`), live eval `--mode live`. The precision gate applies to all configurations, not only the production one.
  - _Spend on live calls:_ $0.67 recording + $0.24 variance = $0.91 (approved limit $2.50).
  - _For the developer:_ spot check of 14 payments from the gold set (table in `evals/dataset/gold-v1/REVIEW.md`); corrections would go to `gold-v2`.
- [x] **Phase 6** – **MCP server** + guide for Claude Desktop / Code.
  - _Done 2026-11-12._ Built:
    - `src/PaymentMatching.Mcp` (`payments-mcp`): the official C# MCP SDK 2.4.0, transport stdio (default) and Streamable HTTP (localhost only, `Host` header filter),
    - tools `list_unmatched`, `get_payment`, `get_customer`, `search_invoices` (read only) and `propose_match` (only a proposal into the queue `proposals.jsonl`, idempotent, with the `ProposalValidator` checks), MCP prompt `investigate_unmatched`,
    - statement text only under `untrusted_bank_data` + `injection_suspected`, server instructions and tool descriptions,
    - 37 tests (tools, injection, idempotency, audit log unchanged, a real MCP client over in-memory streams: tool list, annotations, schemas without tenant/path, errors, prompt), 256 in total,
    - ADR 0010, guide in the README (Claude Code, Claude Desktop, HTTP), command in AGENTS.md.
  - _Deviations from the SPEC:_
    - the parameter `invoiceIds` is called `invoiceNumbers` (in the domain an invoice is identified by its number),
    - instead of a tenant a "workspace per process" (data and state directory from the command line, no tool chooses it); tenant isolation in the data layer comes in phase 7 and MCP moves to it (ADR 0010),
    - the server reads the audit log once at start (restart after a new pipeline run).
  - _Spend:_ $0, the server calls no model, no `record`/`live` run.
  - _For phase 7/8:_ the queue of MCP proposals should show in the UI next to the pipeline's proposals (who proposed: `agent:mcp`).
- [x] **Phase 7** – **Multi-tenant + sign-in** – Microsoft/Google login, a sandbox per person, starter data, isolation tests.
  - _Done 2026-11-12._ Built:
    - `src/PaymentMatching.Data` (EF Core + Npgsql, migrations): tenants, users, data, decisions, invoice matches, proposals; `TenantId` in the primary key of every table,
    - isolation in `PaymentsDbContext`: a query filter on every entity, a write check (foreign row → `TenantIsolationException`), nothing without a tenant (fail closed); across tenants only `TenantAdmin` (guarded by a test),
    - idempotency from ADR 0006 as primary keys in PostgreSQL (`decisions`, `invoice_matches`), the pipeline through `IDecisionStore` (JSONL stays for the CLI and evals),
    - `SandboxProvisioner`: first sign-in = tenant + starter data (20 payments, ~25–30 invoices) + only the rules layer, AI payments wait for the daily job; a daily limit of new sandboxes; deletion after 30 days of inactivity (`TenantAdmin.DeleteInactiveAsync`, scheduled in phase 9),
    - `src/PaymentMatching.Web`: cookie + OIDC Microsoft (common, issuer validated by `tid`) and Google, code flow + PKCE, tokens are not stored; dev login only in Development; read-only API (`/api/me`, `/api/payments`), `/privacy`, security headers, sign-in rate limit,
    - `payments-mcp --tenant <id>` over the tenant data layer, proposals in the database,
    - 44 new tests (302 in total) against a real PostgreSQL (Testcontainers): leaks between tenants with the same ids in all tables, over HTTP and MCP, concurrency, limits, OIDC redirects without network and without secrets,
    - ADR 0011, update of ADR 0006/0010, README (running locally, app registrations), AGENTS.md, `.env.example`.
  - _Deviations from the SPEC:_
    - no demo without sign-in yet (optional, moved to phase 8 with the UI),
    - deletion of inactive sandboxes is done as a function, the daily job will run it (phase 9),
    - RLS in PostgreSQL as a second layer not yet (candidate for phase 11).
  - _Spend:_ $0, no model call.
  - _For phase 8:_ UI over `/api/*` (only simple HTML so far), confirm / reject / match manually as a person's first writes (the "no endpoint takes text" test then extends to validated actions), MCP proposals in the queue.
- [x] **Phase 8** – **UI** – list of payments, "who decided", queue for a person (confirm / reject / match manually), audit timeline, dashboard.
  - _Done 2026-11-12._ Built:
    - UI as Razor components rendered on the server through explicit minimal API endpoints, **without JavaScript** (CSP without scripts, only `style-src 'self'`), no new packages: dashboard, payments list with a state filter, queue, detail ("who decided": rule / model + prompt + confidence + reasoning, pipeline and `agent:mcp` proposals side by side, audit timeline including model calls with tokens and cost), manual match with a preview of the checks,
    - table `reviews` (PK = idempotency key of the statement line → one human decision per payment, never overwritten) + human matches in `invoice_matches`; the pipeline and MCP treat them as taken, MCP does not propose for a payment decided by a person,
    - `ReviewActions` (confirm / reject / manual) through the tenant context; `ProposalChecks` shared with MCP: blocking failures (invoice already matched, different customers, unknown invoice) vs. warnings a person must tick one by one (the server wants exactly today's set),
    - antiforgery (`__Host-` cookie, SameSite=Strict) on all writes including sign-out, a rate limit on actions per person, inputs only ids and known check codes,
    - dashboard: automation, human interventions, caught injections, AI cost per day and per 1,000 payments, "live precision" of proposals by proposer (rule / ai / agent:mcp),
    - `match --tenant <id>`: a stub run over a sandbox (no model call) for a local demo of the AI parts,
    - 20 new tests (322 in total): actions and concurrency in the database, leaks between tenants with the same ids in the data layer and over HTTP, CSRF (no token / token of another session), encoding of untrusted text from the statement, model and agent, free text refused, extended test of write endpoints (exact list + allowed parameter types), pipeline after a human match, MCP over decided payments,
    - ADR 0012, README (what the UI can do), AGENTS.md.
  - _Checked by hand in a browser_ (alice, locally): queue, confirming without a ticked warning saves nothing, with the tick it saves, timeline and dashboard with live precision.
  - _Deviations from the SPEC / plan:_
    - demo without sign-in moved to phase 9 (makes sense only with data processed by the job),
    - a person's decision cannot be undone (append-only audit; an "undo" would be a new row),
    - queue = `human_review` + `no_match` with an agent's proposal; payments waiting for an AI run have no actions,
    - the AI budget on the dashboard comes with the job (phase 9).
  - _Spend:_ $0, no `record`/`live` run.
- [x] **Phase 9** – **Daily job** – generator + automatic processing, limits and AI budget (refined 2026-11-12, ADR 0013, SPEC 9b).
  - _Done 2026-11-12._ Built:
    - `src/PaymentMatching.Job` (`payments-job`): a "one run and exit" console job for a later Container Apps Job, `--mode stub|live`, `--migrate`; deletes inactive sandboxes (30 days, never the demo), creates the demo, adds the shared batch only to active sandboxes (7 days) after something was resolved or with a queue < 3 (demo always), then rules → AI → validation,
    - a shared daily batch `DailyBatch` (seed = date, 3–5 payments, 1–2 for the AI layer; verified over 400 days), ids with the date (`P261112-0001`); the generator got options for prefixes and number series (defaults = `data/sample` unchanged),
    - a response cache `CachingProvider` under the record/replay key (+ source `live`/`stub`), a cached answer costs $0, does not count towards the limit and passes validation in every sandbox,
    - `OperationsDbContext` (own migrations): `ai_response_cache`, `daily_usage`; budgets `AiAllowance` = one number per sandbox from three layers; sandbox limit → the payment waits for the next day, global day / month → to a person labelled "AI budget used up"; `Ai:SmallModelOnly`,
    - web: `/demo` (no sign-in, read only, tenant from configuration + `IsDemo`, read-only context, rate limit), `/admin` (allowlist, otherwise 404, summaries only), AI budget on the dashboard, label in the list and detail, cached answer in the audit timeline, counting of refused sign-ins (waiting list),
    - 39 new tests (361 in total), ADR 0014, `docs/cost.md`, README, AGENTS.md, `.env.example`.
  - _Checked by hand locally:_ the job in stub mode over the local database (3 sandboxes, the second run added and called nothing, a sandbox over the limit left 4 payments waiting), `/demo`, `/admin` and the dashboard in the browser.
  - _Deviations from the SPEC / plan:_
    - payments over the **sandbox** daily limit wait for the next run (decided with the developer); the "AI budget used up" label only when the global daily limit or the monthly budget is used up,
    - an injection without candidates is decided by the policy without a model, so on some days the batch does not need the model at all,
    - the monthly budget is guarded by a reserve of $0.05 per call (not by an exact price up front),
    - two concurrent job instances could exceed the limit by a call; in Azure parallelism 1 + a spend limit in the console.
  - _Spend:_ $0.0007. One live run of the job over the local database with the developer's consent (estimate ≤ 5 calls, < $0.02). Actual: 2 Haiku calls in the only sandbox that still had its daily limit; the other sandboxes had used it up that day with a stub run, and because the ledger also counts stub calls, their payments wait for the next day. Result: 1 automatic match (confidence 0.96) and 1 payment for a person (0.80).
- [x] **Phase 10** – **Observability** – OpenTelemetry, cost and tokens in traces, Application Insights.
  - _Done 2026-11-12._ Built:
    - libraries (gateway, pipeline, job) write only through the BCL (`ActivitySource` + `Meter` "PaymentMatching" in `AiGateway/Telemetry.cs`), no new dependency,
    - spans `matching.run` → `matching.payment` (layer, rule, outcome, codes) → `ai.complete` → `chat {model}` (GenAI conventions: model, tokens; plus tier, cost, cached, provider latency, error category); `job.run` → `job.sandbox` with a tenant pseudonym (12 hex of SHA-256),
    - metrics `payments.ai.calls|tokens|cost|call.duration|budget_exhausted`, `payments.matching.decisions`,
    - `src/PaymentMatching.Observability`: SDK and exporters only in the hosts (web, job, MCP, `Match.Cli`), switched on only by configuration (`OTEL_EXPORTER_OTLP_ENDPOINT`, `APPLICATIONINSIGHTS_CONNECTION_STRING`), a redaction processor before export; ASP.NET Core instrumentation without static files,
    - Aspire dashboard in `docker compose --profile observability` (localhost only),
    - 18 new tests (380 in total) including leak tests: replay of real answers over `data/sample`, the job over a sandbox, the web with `?name=` and a user agent, MCP with marked arguments; a tag allowlist,
    - ADR 0015, README, AGENTS.md, `.env.example`.
  - _Checked by hand:_ `Match.Cli --mode replay` with export to Aspire – a tree of 463 spans, the detail of `chat claude-opus-5-5` with tokens, cost and latency, metrics `payments.ai.*`.
  - _Deviations from the SPEC / plan:_
    - Npgsql spans left out (metrics only): a database error carries values in its message,
    - logs are not exported (they stay on stdout),
    - Azure Monitor only as an exporter, not the whole "distro" (that also switches on logs and live metrics); the real connection to App Insights in phase 11,
    - the job over the local database was not run with the stub (the stub counts calls into the sandbox's daily limit); the job's spans are covered by tests over Testcontainers.
  - _Spend:_ $0, no `record`/`live` run.
- [x] **Phase 11** – **Azure** – Bicep/azd, Container Apps + Job, PostgreSQL, Key Vault, deployment from GitHub Actions through OIDC, custom domain (subdomain of stanislavmatzke.com).
  - _Done 2026-11-13._ Runs at **https://payments.stanislavmatzke.com** (managed certificate). Built:
    - `infra/main.bicep` + `main.bicepparam` (the application's state committed): `rg-payments` – Container Apps environment, web `ca-payments-web` (0.25 vCPU, 0–1 replica, scale to zero), job `caj-payments-daily` (05:00 UTC, parallelism 1, no retry, `--mode live --migrate`), Key Vault (RBAC), Log Analytics (cap 0.1 GB/day) + App Insights, identities `id-payments` (runtime, Key Vault Secrets User) and `id-payments-deploy` (GitHub OIDC, Contributor only on the resource group),
    - scripts `deploy-infra.sh` (local, keeps the running image), `bootstrap-db.sh` (own database `payments` and role `payments_app` on the shared server, temporary firewall rule), `bind-domain.sh`,
    - `deploy.yml`: after green CI on `main` images to ghcr.io (public packages), through OIDC an update of the app and the job, 7 smoke tests; CI additionally compiles the Bicep and builds the images without Azure,
    - the app: Data Protection keys in PostgreSQL (no package, sign-in survives scale to zero), forwarded headers behind the ingress, image through `dotnet publish` (chiseled), `/api/me` shows `accountId` for `/admin`,
    - 3 new tests (383 in total) and an `accountId` check in the sign-in test, ADR 0016, `docs/cost.md` (hosting), README "Running in Azure", AGENTS.md.
  - _Checked in Azure:_ smoke tests from the workflow, sign-in with Microsoft and Google (developer), `/admin` only for the admin account, a live job run (demo + 10 decisions), 463 spans in App Insights without statement text.
  - _Deviations from the plan:_ one `main.bicep` instead of modules; Data Protection through an own `IXmlRepository` instead of a package; the database on the shared server of another project of the developer (decided with the developer, ~$16–18/month saved); the developer ran the Bicep and scripts in Azure (Claude Code's safety check did not allow the agent to apply infrastructure).
  - _Spend:_ hosting estimate ~$0–2/month; AI $0.0306 (8 calls, first job run: demo + batch; the estimate was < $0.01).
  - _Open / next step:_ a new person sees AI payments as "waiting for AI" until the morning job – the developer considers that a product bug; a proposed solution (shared starter data with the response cache) for approval. UI: invoice detail in the payment detail, an Admin tab, sign-in buttons.
- [x] **Phase 12** – **Documentation and preparing publication** – README, ADR, security, cost, modernization, how-it-was-built + bilingual docs and clean-up before the repository is made public:
  - _Done 2026-11-13._ Built:
    - English `docs/SPEC.md`, `docs/PLAN.md`, `AGENTS.md`, `CLAUDE.md` (the agent reads them; "communicate in Czech" stays); Czech versions in `docs/cs/` (SPEC, PLAN, modernization, how-it-was-built) and `README.cs.md`; the original Czech SPEC and PLAN moved with `git mv` to `docs/spec-original-cs/` (frozen, personal notes removed),
    - README rewritten (problem for a manager in three paragraphs, Mermaid architecture, eval table, live app and demo, what the app shows, 2-minute start, spec-first workflow, details folded),
    - new `docs/security.md` (OWASP Top 10 for LLM applications → defences and the tests behind them; the SPEC had asked for it since the start), `docs/how-it-was-built.md` (+ Czech), `docs/adr/README.md` (index 0001–0018), `docs/cost.md` with production values and a comparison with manual work,
    - flaky test fixed (cause below), "phase N" comments in code replaced with ADR references, an unused test package removed, no dead types found,
    - checks: gitleaks over the whole history in CI, all 87 earlier commits with the GitHub noreply address, no local paths or personal e-mails in the history or the tree, every relative Markdown link resolves (script), `LICENSE` (MIT),
    - 401 tests, five full runs green, format clean, eval replay gate passed.
  - _Deviations:_ the "Job Scout" mentions were removed from the SPEC and README and replaced with "another project" in the English PLAN; the Czech notes and ADR 0014 keep them (the developer's own public project). ADRs stay English only.
  - _Spend:_ $0, no model call, no change in Azure.
  - **The main language of the repository is English.** `README.md`, `AGENTS.md`, `docs/SPEC.md`, `docs/PLAN.md` and all documents in `docs/` are in English.
  - **Czech version alongside:** `README.cs.md` and `docs/cs/` (SPEC, PLAN, modernization, how-it-was-built). Every document starts with a link to the other language ("🇬🇧 English / 🇨🇿 Česky").
  - **The original brief:** the Czech `docs/SPEC.md` and `docs/PLAN.md` moved without content changes to `docs/spec-original-cs/` as the authentic start of the project (only with the clean-up below). The translations are based on them.
  - **AGENTS.md:** the English version is the one the agent reads. The rule "communicate with the developer in Czech" stays.
  - **Clean-up of personal notes:** remove the developer's personal notes from public documents. A neutral SPEC introduction ("Reference implementation showing…"). Personal outputs go to a file outside git.
  - **README** in both languages: problem → architecture (Mermaid) → eval results (table) → link to the live app → run it in 2 minutes → "Built spec-first with an AI coding agent – see AGENTS.md, docs/SPEC.md, docs/PLAN.md" → MIT licence.
  - **Check before publication:** gitleaks over the whole history, no real data, no secrets, no internal paths or e-mails, links in documents work, `LICENSE` exists.
  - **Added after phase 11 (2026-11-13):**
    - **Flaky test:** `McpTelemetryTests.Tool_calls_are_traced_by_name_without_arguments_or_error_text` sometimes fails in the whole parallel suite, passes alone. Find the cause and fix it, do not skip it. Verify with several full runs.
    - **Code review before publication:** go through `src/` and `tests/` (dead code, outdated "phase N later" comments, TODOs, inconsistent names), `dotnet format`, no warnings; Bicep and scripts in `infra/` (comments, no personal ids except deliberate ones: the operator's name and contact, client id, OIDC subject, domain).
    - **README:** remove "Work in progress", link to the live app https://payments.stanislavmatzke.com and the demo, eval results, what the app shows (Czech, "What AI added", undoing an AI match), deployment to Azure.
    - **`docs/how-it-was-built.md`** (+ Czech version) from the progress notes: spec-first with an agent, approving plans, the agent's mistakes and how tests caught them, what only deployment revealed, user testing after launch.
    - **ADR index** (0001–0018) in `docs/adr/README.md`.
    - **Publication check:** ghcr.io packages are public; `.gitleaksignore` explained; no secrets or personal e-mails in the history (except the deliberate contact and the operator's name in `infra/main.bicepparam`).
    - **Verify that the live app works after the changes** (smoke tests from the deploy workflow + a manual check in EN and CS).
  - At the end a list of files to review; the developer makes the repository public.

Order 1–6 is the priority (the repository with modernization, AI matching, evals and MCP had to be presentable first). Phases 7–11 turn the project into a live application.

## Progress notes

_(the agent writes here continuously: decisions, problems, what it got wrong and how it was fixed – source for [docs/how-it-was-built.md](how-it-was-built.md))_

**Phase 1 (2026-11-12)**
- The agent first added the classic trio of test packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`). On the .NET 10 SDK with xUnit v3 that failed ("VSTest target is no longer supported"). Fix: `global.json` → `test.runner = Microsoft.Testing.Platform`, two packages removed. Fewer dependencies.
- `dotnet format` on Windows reported line-ending errors for files written with LF. Fix: `end_of_line = lf` in `.editorconfig` + `.gitattributes` (`eol=lf`), so formatting comes out the same locally and in Linux CI.
- A risk the agent handled up front: a VS with swapped digits could accidentally equal another existing invoice (the "typo" would then really be the correct VS of another invoice). The generator switches such collisions to the missing-digit variant (a 9-digit VS cannot collide). Covered by a test.
- Rounding of case weights: `0.06 * 300` in floating point can give 17.999…, hence an epsilon in the count planner.
- The first push was refused by GitHub (private e-mail protection in commits). Fix: commit author set to the GitHub noreply address in the repository, 4 local commits rewritten before the first push. Benefit: no personal e-mail in the future public history.
- First CI run: `gitleaks-action@v2` failed on the first push of a new repository (invalid range `root^..HEAD`, scanned nothing). Fix: gitleaks CLI with a verified checksum over the whole history. Lesson: a "green" secret scan has to be read – the action reported "no leaks found in partial scan" over 0 bytes.

**Phase 2 (2026-11-12)**
- While designing the legacy rules the agent found that the first version of the "partial payment" rule could be abused by the message text: an injection "payment belongs to invoice 2026-xxxx" with a smaller amount would be matched as a partial payment. Change: partial payment only when the VS came in the VS field (realistic exception "2007-03 JN"). Legacy thus resists injection, but only by accident, and `docs/modernization.md` says so explicitly.
- An unplanned but valuable finding in the data: a wrong fuzzy match "uses up" an invoice, so a later correct payment for it ends as `DUPLICATE` (7× in the 1,000 set). One mistake does two kinds of damage.
- The agent wrote a one-off comparison script in awk and used the variable `NR` in it, which is awk's built-in variable (line number). The first "without a person" numbers were nonsense (636 of 300 payments). The error was caught by checking the totals and fixed. Lesson: even a one-off measuring script needs a check.

**Phase 3, step 1 – extracting the rules from legacy (2026-11-12)**
- The extraction was done by an agent in a **separate session without context**. It could read only `legacy/delphi`, `legacy/plsql`, `legacy/README.md`, `docs/data-format.md` and `AGENTS.md`. It did not run the code, did not see the golden master, PLAN, modernization or the answer key. By its own report it kept to that.
- Result: `docs/business-rules.md`, 24 rules, 15 differences Delphi × PL/SQL, 13 risks with concrete inputs, 12 open questions.
- **Score against the answer key (outside git): 13 / 13.** It found all 11 planted rules including the deliberate fuzzy bug, its domino effect (`DUPLICATE`) and the `<` × `<=` tolerance mismatch, and both "minor" items.
- **It also found things the author of the legacy code (another agent session) did not plant deliberately**, all verified against the code:
  - the PL/SQL window of 26 characters cuts off the invoice number: `FAKTURA PRO FIRMU ABC 2026-1234` → `2026-1` → another invoice,
  - empty currency: Delphi refuses, PL/SQL matches as CZK (NULL in the condition),
  - unreadable amount = 0 → an automatic "partial payment" that closes the invoice,
  - a partial payment has no lower bound (CZK 1 closes an invoice),
  - the invoice currency is not checked,
  - `ROWNUM = 1` without `ORDER BY` is nondeterministic in PL/SQL,
  - `payment_id` sorted as a string.
- Check: 17 line references verified at random, all exact. No hallucinations found. The only correction: the examples contained the name "NOVAK JAN" (realistic-sounding) → replaced with a fictional company.
- Insight for the article: the AI was **more thorough in understanding the code than the author of the fiction**. The most valuable part are not the rules themselves but the edge cases with concrete inputs, from which tests can be written directly.

**Phase 3, port (2026-11-12)**
- The faithful port reached byte identity with legacy **at the first attempt** on 1,300 payments. It helped that business-rules.md had an exact description of the output format and the advice to read text "by bytes" (Latin-1).
- **A characterization test corrected the AI document:** two unit tests taken from examples in business-rules.md failed while the golden test passed. The rule about the message window had an off-by-one error (0–14 instead of 0–15). Verified with a probe against the legacy binary, document corrected with a note. Lesson: AI extraction is a great start, but line-level accuracy is verified only by running code.
- `double` → `decimal` (F3) without a single output change. The golden master made it a safe refactoring.
- The test "every difference must be explained" **twice caught unforeseen consequences of F1**: a `DUPLICATE` can also become `OVERPAID`, and the VS can also come from the message. The agent had not thought it through in the first draft of the classifier; the test corrected it. Exactly what the test is for.
- The legacy fuzzy bug also blocked correct payments from third parties and with the VS in the message (2 cases in the 1,000 set), not only exact matches.

**Phase 4 (2026-11-12)**
- The plan was made in a new session only from PLAN/SPEC/ADR/business-rules, without questions. The agent proposed the Q-02/Q-07 policy only after joining the port's output with the ground truth by case type.
- **Validation was broken by the stub, not the model.** The deterministic stub (takes the code's first suggestion) matched an EUR payment with a combination of two other invoices of the same customer. The rate band is ~10 % wide, so the sum "fitted" into it. A test with a random model had passed that **by chance**. Fix:
  - test replaced by an exhaustive one: every set of 1–4 candidates as "the model's answer" over 1,300 payments; it found 8 + 37 holes, all EUR,
  - two new validation rules: the payment must name the invoice (VS / number in the message) and EUR pays only one invoice,
  - then 0 holes.
- The replay test first failed: the CLI and the tests had different `MaxTokens`, which are part of the recording key. Fix: one definition of models (`ModelSettings`).
- **CI revealed an OS dependency of the prompt.** Recordings were made on Windows, in CI (Linux) replay did not find the key. `WriteIndented` JSON in the prompt used `Environment.NewLine`, so the model got different text depending on the OS (`\r\n` vs `\n`). Fix:
  - fixed `\n` + a test "the prompt contains no `\r`",
  - new recording (the second time ~$0.20).
  - Lesson: the recording key = a fingerprint of exactly what goes to the model, and it must be the same on every platform. Without CI on another OS it would not have been found.
- Estimate of the recording cost "< $0.20": first run $0.205, second $0.19. 84–85 % of the cost is escalation to Opus.
- Small things caught by build/test: an unclosed `sed` in a heredoc (files not written), `--` in an XML comment in a csproj, `JsonException` instead of `FormatException` from the parser, comparing records with lists by reference.
- The model (Haiku) claimed in one reasoning that the message names both invoices while it named one. The decision was right, the reasoning was not. That is why reasoning is not evidence and validation decides.
- Permission prompts in Claude Code slowed work down; the developer allowed common commands in `.claude/settings.local.json` (outside git).

**Phase 5 (2026-11-12)**
- The plan was again made in a new session only from PLAN/SPEC/ADR and code, approved without changes.
- **The first eval run found a hole in validation.** Stub over the gold set: precision 84.2 %, 8 payments on the wrong invoice. All 30 ties (two invoices or combinations with the same amount) passed validation: it checked that a proposal *fits*, not that it is *the only one*. The gate failed the build. Fix: rule `AMBIGUOUS` + the exhaustive validation test now also over the gold set and stricter (a valid proposal must be the only correct one and may be automatic). Lesson: the exhaustive test from phase 4 was a good tool over data that contained no ties. A test is only as good as its data.
- The real added value of the model on this data: injection without keywords (30/30 caught, the stub proposed the target to a person 20×). On unreadable invoice numbers the model does not help, because an automatic match may only be what code verifies. The parser was deliberately not extended to the generator's formats (overfitting to own data).
- The new escalation rule cut Opus calls 39 → 4 with the same results. The decision is code (the same validator), not the model's judgement.
- The recording run in `record` mode also included the threshold sweep: another threshold changes which invoices are matched earlier, so later payments get different candidates and 25 extra Haiku calls happened (~$0.01). Harmless but instructive: "a free sweep" holds only for replay.
- Small things the agent got wrong: heredoc + Python (Windows Store stub) wrote nothing → Write/Edit; `sed` with backslashes in csproj paths silently replaced nothing (twice, caught by the build); `sha256sum` on Windows writes `*` before the file name, so the first manifest did not match (caught by the runner at the first run).

**Phase 6 (2026-11-12)**
- The plan was again made in a new session only from PLAN/SPEC/ADR and code, approved without changes. The only deliberate deviation: tenant only in phase 7, until then "workspace per process" (written in ADR 0010).
- **A protocol test caught a bug the compiler cannot see:** `WithPrompts(typeof(InvestigatePrompt))` bound to the generic "target instance" overload (`TPromptType target`) with a `Type` object, so no prompt was registered. Unit tests over the class would have passed; only the test through a real MCP client failed (`prompts/list` "not available"). Fix: `WithPrompts([typeof(...)])`. Lesson: test DI/attribute registrations through the protocol, not only the class.
- `dotnet run` for a project with the Web SDK starts the application in the project directory, so the default relative paths (`data/sample`) did not exist. Found by a manual smoke test over stdio. Fix: `RunWorkingDirectory = $(MSBuildStartupDirectory)`.
- Smoke test over stdio: the first few attempts returned nothing, because the end of stdin stops the server before it sends the answers. The test script had to keep stdin open. Also verified that `dotnet run` does not write build output to stdout (it would break JSON-RPC).
- The HTTP transport got a `Host` header filter (loopback only), so a web page cannot reach the server on localhost through DNS rebinding. Verified with curl: a foreign Host → 400.
- `sed` with backslashes in a csproj path silently replaced nothing for the third time (adding `InternalsVisibleTo`); this time the agent noticed at once from the file listing and switched to Edit.

**Phase 7 (2026-11-12)**
- The plan was again made in a new session only from PLAN/SPEC/ADR and code, approved without changes.
- **Leak tests deliberately with the same ids in both tenants** (`P0001`, the same invoice numbers): only the filter tells them apart. The list of tested tables is taken from the EF model, so a new table without a tenant fails the test by itself.
- **The first tests against a real database** (Testcontainers). Unique keys and concurrency (two runs decide the same payment, two first sign-ins at once) cannot be tested honestly over an in-memory provider.
- OIDC without network and secrets: the test injects the provider's metadata and checks the redirect (authority, `client_id`, scope, PKCE, callback). The real sign-in through Microsoft/Google is verified by the developer with an own app registration.
- Small things the agent got wrong:
  - a one-off probe "is python here?" through a heredoc started an interactive REPL on Windows and hung for 2 minutes → stopped, from then on only Write/Edit,
  - `sed` with backslashes in a csproj path silently replaced nothing for the fourth time; this time visible at once in the check listing, fixed through Edit,
  - a test helper cut a database name `[..40]` from a string that was shorter → 9 tests failed on `ArgumentOutOfRange`, not on isolation,
  - minimal API cannot bind `?full=1` to `bool?` → 400; caught by a test,
  - Npgsql pulled in EF Core 10.0.4, the `dotnet-ef` tool was 10.0.3 → aligned.
- Operations: a running `payments-mcp` (from `.mcp.json`) locks the Debug output, the build failed on locked DLLs. Locally therefore build/test in Release; written to AGENTS.md.

**Phase 8 (2026-11-12)**
- The plan was again made in a new session only from PLAN/SPEC/ADR and code, approved without changes.
- **The most important design decision was not in the SPEC:** "validation as for the model" taken literally would make the queue useless – a person is there precisely for the cases that do not pass validation (partial payments, third parties). Split into blocking failures and warnings ticked one by one, and the server wants exactly the current set (a stale page or a hand-made request skips nothing).
- **A hidden link between phases:** after a human match the daily job (phase 9) would have failed on the primary key of `invoice_matches`, because `DbDecisionStore` knew only automatic matches. Handled and covered by a test before the job existed.
- Data layer tests passed at the first attempt; HTTP tests except one wrong expectation in a test (it looked for a label of a list row on the detail page).
- Small things the agent got wrong:
  - it overwrote `Pages.cs` with new content and with it the `SecurityHeaders` class; it noticed right after writing and moved it back to its own file,
  - the variable `code` in Razor collides with the `@code` directive (caught by the build),
  - a shell script with several heredocs did not parse as a whole and wrote nothing → the rest through Write,
  - an MCP test assumed the check `INVOICE_ALREADY_MATCHED`, but the server refuses a taken invoice earlier with an exception; fixed before running,
  - **a new MCP test depended on chance:** it took "the second open payment" from the sandbox's starter data, whose seed is derived from the random tenant id. Locally it passed, in CI the data had no open payment → `ArgumentOutOfRange`. Fix: the test inserts the payments and decisions it needs. Lesson: a test over generated data either with a fixed seed or with its own rows.
- Operations: locally an older web version from another session ran on 7180, the new version was checked on 7181 (same database, the migration only adds a table).

**SPEC change before phase 9 (2026-11-12)**
- Before phase 9 the developer questioned "5–10 new payments a day in every sandbox": a public demo should not burn tokens on data nobody looks at. The agent computed the cost from the evals (≈ $0.0008 per AI payment, the original plan ~$1.5–12 a month depending on the number of people) and proposed refilling by activity, a shared daily batch with a response cache (cost independent of the number of people), three budget layers and a "small model only" mode. Approved in full, including the demo without sign-in and `/admin` with summaries only. Written into SPEC 9b and ADR 0013.
- Insight: the response cache is just the production use of record/replay from phase 4. A tool built for tests without an API key returns as a saving in operation.

**Phase 9 (2026-11-12)**
- The plan was again made in a new session only from PLAN/SPEC/ADR and code. Before approval the agent asked the one thing ADR 0013 had not settled: a new sandbox has ~5–7 AI payments in its starter data, but a limit of 2 calls a day. A literal reading ("to a person with a label") would show a new person 3–5 "budget used up" labels at once. The developer chose: over the sandbox limit wait for the next day, the label only for the global budget.
- **Measuring instead of assuming:** the first version of the batch chose "rule" cases by intuition. A test over 400 days failed (3 payments for the model instead of 1–2); a one-off probe showed that `MultiInvoice` always goes to AI and partial payments and VS in the message only sometimes. The batch pools were adjusted by the numbers and the test holds it.
- A second finding from the test: an injection without candidates is decided by the policy without a model, so "an AI case" does not always mean "a model call". The cache tests therefore pick a day when the batch really needs the model.
- **A budget trap found during design:** the gateway checked the limit *before* calling the provider, so with the limit used up it would not even let through an answer available from the cache for free. Added `IAnswerCache` + a test "over the limit a cached answer is used, the model is not".
- A risk the agent handled up front: a cache shared between `stub` and `live` would store the stub's answer in the local database under the key of the real model and a later live run would return it. The cache key therefore also has the source.
- Small things the agent got wrong:
  - a shell script with several heredocs again did not parse and wrote nothing → Write,
  - `sed` with backslashes in a csproj path silently replaced nothing for the fifth time (caught by a `grep` right after it) → Edit,
  - `--` in an XML comment in a csproj for the second time (build),
  - a test with GUID order would pass by chance (comparing a sorted set with a list in fixed order) – fixed before the commit,
  - joining two interpolated strings with `+` inside `FormattableString.Invariant` does not compile (the result is `string`).
- After the phase the developer questioned the limit of 2 calls per sandbox and day: "2 / 2" on the dashboard looked ridiculous when a call costs a fraction of a cent, and new sandboxes waited several days for AI. The protection against "being fleeced" is held by the global limits and the number of new sandboxes, not by the sandbox limit. Changed: 20 calls per sandbox and day, 5 new sandboxes a day (amendment in ADR 0014). For deployment a monthly budget of $0.50 and a spend limit of $5 in the Anthropic console are recommended.

**Phase 10 (2026-11-12)**
- The plan was made in a new session from PLAN/SPEC/ADR and code, approved without changes.
- **The main finding came from a test, not the design:** for a tool that ends with an error, the MCP SDK puts the whole tool answer into the span status – and it repeats the agent's arguments (e.g. an unknown id). The test with marked arguments caught it at the first attempt; the redaction processor therefore removes the error description from all foreign spans. Lesson: "the library's instrumentation does not record arguments" does not mean it will not record them some other way.
- Design decision: libraries only on the BCL `System.Diagnostics`, the SDK only in the hosts. CI thus needs no backend or extra package, and tests read spans through an `ActivityListener` filtered by their own trace (tests run in parallel, listeners are global).
- Replay reports cost and latency from the recording (like the audit). That is why the span also has `payments.ai.latency_ms` – the span duration in replay is a fraction of a millisecond.
- Small things the agent got wrong:
  - the new project `PaymentMatching.Telemetry` shadowed the class `Telemetry` in the namespace `PaymentMatching.*` (build) → renamed to `PaymentMatching.Observability`,
  - the web telemetry test passed alone but not in the whole suite: the ASP.NET Core instrumentation listens to the whole process and saw the requests of parallel tests (`Assert.Single` found 6 sign-ins). Fix: the test looks only for its own unique paths and markers, never counts,
  - the last request of a test: the span on the test server ends only after the response is handed over → a short wait for the span,
  - analyzers (IDE0062 static local function, xUnit2029 `Assert.Empty` over a filter) – build.
- During the phase the developer added a rule for shared files to AGENTS.md. The agent left that change uncommitted (not its own) and added only its own line with the telemetry commands to the index.

**Phase 11 (2026-11-12/13)**
- The plan was made in a new session from PLAN/SPEC/ADR, code and only reading the existing resources in Azure. Two decisions of the developer: the shared PostgreSQL server of another project (own database and role without admin rights) and images on ghcr.io as public packages. Hosting thus came to ~$0–2 a month instead of ~$20.
- **Claude Code's safety check did not allow the agent to apply the infrastructure** (`az deployment`), even though the developer approved the step. The developer therefore ran the Bicep; the agent did the database bootstrap, the manual job run and the GitHub settings. In practice a good split: a person changes resources, the agent writes code and verifies.
- **Four bugs that only the first deployment showed (no test could catch them):**
  1. Azure Flexible Server creates the `public` schema of a new database owned by `azure_pg_admin` → the first migration "permission denied". The bootstrap now hands the schema to the application's role.
  2. A second run of the bootstrap generated a new password, but Container Apps keeps the loaded value of a Key Vault secret until a new revision (a restart is not enough) → the web and the first job run failed to sign in to the database (the job before calling the model, $0). The script does not change the password without `RESET_PASSWORD=1`.
  3. The Azure Monitor exporter adds sampling with a per-second limit by itself; a short process (job, CLI) has a single trace and it was dropped. Metrics arrived, spans did not. SDK diagnostics ("Sampler added = RateLimitedSampler") → a fixed ratio of 1.0.
  4. GitHub sends a `subject` in the OIDC token with the immutable ids of the owner and repository (`repo:owner@id/repo@id:…`); a federated credential in the old format found nothing. The subject is a parameter.
- `/api/me` did not show the `provider:subject` that `/admin` needs – the developer had no way to find out their id. Added.
- Small things the agent got wrong:
  - six times the same trap with the Windows shell: a probe "is python here?" with a heredoc and `cat` without input hung for 2 minutes → stopped, from then on Edit/Write,
  - arguments of `az … firewall-rule` (`--server-name` vs `--name`) – fixed before running, from the help,
  - gitleaks flagged the public ids of Azure built-in roles in the Bicep as keys (CI red) → variables renamed and `.gitleaksignore` for the first commit,
  - in the developer's PowerShell `bash` starts WSL (error 0x8007274c) → instructions through Git Bash,
  - the estimate of the first live run (< $0.01) was low: $0.031, the demo got the starter data and the batch at once and part of the calls escalated to Opus,
  - one run of the whole test suite had one failure, the repeat passed – a flaky test, not traced.
- **The developer's product finding after launch:** a new person sees AI payments only after the morning job, so the first impression is "waiting for AI". The rule "a visitor never starts a model" still holds. Proposal: shared starter data and the response cache, a sandbox gets the AI decisions from the cache at sign-in without a model call (for approval, a change of SPEC 9b / ADR 0011).

**After phase 11: AI right after sign-in (2026-11-13, ADR 0017)**
- The developer after the first sign-in in production: "AI payments wait until the morning – that is useless, a person must see the AI pre-matching at once." The rule "a visitor never starts a model" stays.
- The solution approved by the developer: the starter data is the same for all sandboxes created in a given week (seed = Monday), the job precomputes it once a week in memory and the sign-in takes the AI decisions from the response cache with a budget of 0 calls (behind the cache is a provider that throws). The developer explicitly wanted to verify that the data stays separate: a test decides a payment in sandbox A and checks the same payment in B.
- The key assumption (the same prompts at sign-in and in the precomputation) is held by one shared method `DecideStarterAsync` and a test "after the job a sign-in has all AI payments from the cache and the model got no request".
- Record/replay from phase 4 thus returns for the third time: tests without a key → cache in operation → an instant first impression for $0.
- Spend: a manual job run for the developer's two sandboxes before the change $0.017 (12 calls); the first precomputation of the starter set (week from 2026-11-09) with the developer's consent $0.029 (9 calls, all 20 payments decided). AI for phase 11 and the changes in total $0.077.

**After phase 11: UI changes from operation (2026-11-13)**
- The developer, at the first work in the queue (`FIELD/ADV_OVERPAY`, an advance overpaid by CZK 100), missed the invoice next to the payment: the detail showed only the invoice number, the full details only on the manual match page. A new "Invoices" card in the detail: all invoices named by the decision, a human decision or a proposal (kind, VS, customer, due date, amount, state) and a comparison of the payment with each named set ("+100.00 CZK paid more than invoiced"). Test: changing an invoice with the same number in a foreign sandbox does not show in the panel.
- An Admin tab in the top bar only for accounts from `Admin:Accounts`; `/admin` now has its own navigation (test: a regular person does not see the link).
- Sign-in page: a card with full-width buttons and logos (Continue with Microsoft / Google), an "or" separator and the demo without sign-in. Logos as inline SVG with `fill` attributes (the CSP does not allow inline styles).
- The agent locally: `localhost` resolved to IPv6 on the developer's machine, where the database does not listen ("connection reset") → `127.0.0.1` for the manual check. An existing admin page test forbade the word "boss" – after adding navigation the admin's own name is there; the test was changed to keep guarding the data of others.
- **List of accounts in `/admin`** (ADR 0014 amendment): the developer wanted to see who signed in. Approving sign-ups by hand was rejected together – a visitor does not start a model, new sandboxes are limited and AI has a cap, an unknown account costs only rows in the database. Card "Accounts": name, e-mail, provider, created and last active, numbers of confirmations / rejections / manual matches, state (active / deleted in N days). No sandbox contents and no tenant ids; a test lists the exact fields of `AccountSummary`, a web test guards that the page has no payment texts or counterparty names. Added to `/privacy` (what the operator sees) and SPEC 9b. While changing a test with a script, the agent accidentally deleted the `[Fact]` of the following test – noticed in the check listing before the build.
- **Before showing the app** (the developer wants to share it before the repository is public): `/privacy` now names the controller and a contact for questions and account deletion (`Operator:Name`, `Operator:Email`, GDPR); limit of new sandboxes 5 → 30 a day (thanks to the weekly starter set without AI cost); the web with one always-running replica instead of scale to zero (no cold start, ~$3–5 a month more). All as parameters in `infra/main.bicepparam`.

**After phase 11: second user test (2026-11-13, ADR 0018)**
- The developer as a user: no favicon; icons on the home page in the corner of the frame; wants a link to their website; the app looks empty (2 in the queue); "AI calls 6" does not match 7 payments from Haiku; it is not clear what exactly the AI did and whether it can be reversed; nobody will understand rule codes (`FIELD/EXACT`). Plus paging, sorting, search and Czech.
- Answers from the code, not by guess: 6 vs. 7 = six paid calls + one answer from the cache (the dashboard counted only paid calls); icons = the selector `.flow span` overrode the icon frame's `display: grid`.
- Built: the card "What AI added" (decided by AI / matched on its own / sent to a person / injections stopped / paid calls vs. cache / **AI precision as people see it**), a "Decided with AI" frame in the detail, the filter "Decided by AI", **undo of an AI match** (`/payments/{id}/undo`, a person's decision `undone_ai`, the AI audit stays, the pipeline does not re-match the freed invoice by itself), human descriptions of all rule codes + the page `/how`, a payments list with search (payer, VS, message, amount, id), filters, sorting and paging by 25 (on the server, without JS), **Czech** (`Text.T`, EN/CS switch in the header, cookie, a test renders all pages in Czech and fails on a missing translation), favicon, an "About this project" link (`aboutUrl` in the Bicep), a starter set of 40 payments.
- Small things the agent got wrong / tests caught:
  - a heredoc with Python in the shell failed again (unclosed quote) → scripts into a file,
  - Razor: `@page` and `@code` as variable names (directives) – twice,
  - Razor encoded Czech characters as `&#x159;` (the browser shows it, the test does not) → `WebEncoderOptions` with the whole Unicode range,
  - the rule legend had the code `FIELD/AMOUNT`, which the rules never return, and lacked `MSG/ADV_OVERPAY` – fixed from the `PaymentMatcher` code,
  - a test with an agent's proposal assumed the proposal was not in the data yet; with 40 payments it was → the test does not create it twice,
  - flaky test: `McpTelemetryTests.Tool_calls_are_traced_by_name_without_arguments_or_error_text` sometimes fails in the whole parallel suite, passes alone (to be solved).
- **Budget reserve per call:** the first precomputation of the starter set with 40 payments stopped after 8 calls ($0.0031), 6 payments waited. Cause: the monthly budget holds a reserve of $0.05 per call (ADR 0014), so with ~$0.42 left a run may make only 8 calls, even though a real call costs $0.0004–0.01. The reserve is now the Bicep parameter `aiReservePerCallUsd` = $0.02 (the $0.50 monthly cap still applies). Spend on the precomputation: $0.0031 + $0.0020 after the reserve change (8 answers from the cache, 6 new calls, all 40 payments decided).

**Phase 12 (2026-11-13)**
- The plan was made in a new session from PLAN/SPEC/ADR, README and code; approved without changes.
- **The flaky test, found by reproducing it, not by guessing:** running only the MCP test project in a loop failed once in ~7 runs with `ArgumentException: Destination array was not long enough` while copying the list of spans. The test's tracer provider listens to the MCP SDK process-wide, so the in-memory exporter appended the spans of MCP tests running in parallel to a plain `List` from other threads; the `lock` in the test protected nothing, because the exporter never took it. Worse, `Assert.Contains("get_payment")` could pass thanks to another test's spans. Fix: the test starts its own root trace, collects spans into a concurrent queue after redaction and keeps only its trace (the MCP client propagates it to the server, now asserted on a server span). 0 failures in 30 runs of the project, 5 full runs green. The third time the same pattern (process-wide listeners × parallel tests) appeared – now a rule in AGENTS.md.
- **The end-of-project check against the SPEC found two gaps no phase had covered:** `docs/security.md` (asked for in SPEC chapter 9 from the start) and the comparison with manual work in `docs/cost.md`.
- Small things the agent got wrong:
  - `python -` in Git Bash started an interactive interpreter and hung again (the seventh time this trap is in the notes) → stopped, the edits done with Edit,
  - the first manual-work comparison in `cost.md` had arithmetic that did not add up (legacy ~$450 instead of ~$470, wrong assumption about which matches are wrong); recomputed from the modernization numbers before the commit.

**After phase 12: final code review and refactoring (2026-11-13)**
- The developer asked for a review "as a principal .NET developer" (structure, naming, clean code, DI, dependencies, the CSS too) and approved 11 of 12 findings. Done in small commits, each with the affected test projects, at the end 3 full runs green, format clean, eval gate passed:
  - `ReviewActions`: one check-and-save path for confirm / manual match / undo, the payment no longer passed around as nullable, `CancellationToken` last (`PreviewUndoAsync` instead of a flag after the token); the two picker POST endpoints share one handler,
  - `SandboxQueries` (444 lines) split into `SandboxViews`, `SandboxQueries` and `PaymentTimeline`,
  - `DecidedBy` is the one place that reads "rule:…", "ai:…", "agent:…", "person" and "policy" (five files had their own prefix checks); the MCP server uses `ProposalChecks.MaxInvoices`,
  - the daily job: gateway usage (`AiGateway.Usage`), one provisioner, the shared model routes; web service registration moved to `AddPaymentsWeb`, `ReviewActions` resolved by DI,
  - `CallError` enum on failed model calls instead of parsing the "transport: " prefix back,
  - one shared `RepoPaths` for the tests (five copies), the last hard-coded CSS colors as tokens, configuration packages aligned to 10.0.3, stale comments (ADR 0012 amendment, "three actions").
- Not done on purpose: reusing the pipeline's `CandidateSelector` for the validator (a field initializer cannot use another field; an explicit constructor for a cosmetic gain), splitting `PaymentPage.razor` (the developer left it optional).
- Small things the agent got wrong: `sed` with backslashes in a csproj path silently wrote `..SharedRepoPaths.cs` (the sixth time this trap is in the notes; caught by the check listing, fixed with PowerShell); the agent's own phase-12 comment in `Program.cs` said "three actions" right after adding the fourth to the list.
- **A red CI run that was not our code** (1d99a17, first attempt): Docker Hub's CDN broke the download of `postgres:17-alpine` for one of the four test projects that pull it in parallel; Testcontainers logged "image created", then failed with "No such image" and does not retry. The re-run passed. CI now pulls the image once with retries before the tests.
