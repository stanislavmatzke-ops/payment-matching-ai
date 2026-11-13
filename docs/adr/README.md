# Architecture decision records

Short records of the decisions that shaped the project: the context, the decision, the alternatives
and the consequences. Later decisions that change an earlier one say so in both records.

| ADR | Decision | In short |
|---|---|---|
| [0001](0001-dotnet-10-postgres-and-repo-layout.md) | .NET 10, PostgreSQL and repository layout | one solution, central package management, warnings as errors |
| [0002](0002-synthetic-data-generator-with-ground-truth.md) | Synthetic data generator with ground truth | deterministic by seed; every payment knows its correct answer |
| [0003](0003-legacy-in-free-pascal-docker-golden-master.md) | Legacy in Free Pascal (Docker), golden master | the fictional Delphi module really runs; its output is the reference |
| [0004](0004-faithful-port-first-then-deliberate-changes.md) | Faithful port first, then deliberate changes | byte-identical with legacy, then every difference must be explained |
| [0005](0005-thin-ai-gateway-in-dotnet.md) | A thin AI gateway in .NET | routing, limits, cost per call; why not Azure API Management or LiteLLM |
| [0006](0006-structured-output-validation-and-human-in-the-loop.md) | Structured output, validation by code, a person in the loop | the model chooses, code decides, uncertainty goes to a person |
| [0007](0007-record-replay-for-model-calls.md) | Record/replay for model calls | tests and CI without an API key; later the production response cache |
| [0008](0008-evals-frozen-gold-set-and-ci-gate.md) | Evals on a frozen gold set with a CI gate | precision ≥ 99 % and no matched injection, or the build fails |
| [0009](0009-escalation-only-when-it-can-change-the-outcome.md) | Escalate only when it can change the outcome | same results with 4 instead of 39 large-model calls; threshold 0.90 kept |
| [0010](0010-mcp-server-read-tools-and-proposals-only.md) | MCP server: read tools and proposals only | least privilege for agents; statement text labelled as untrusted |
| [0011](0011-tenant-isolation-in-the-data-layer-and-oidc-sign-in.md) | Tenant isolation in the data layer, OIDC sign-in | a filter on every entity, fail closed; Microsoft / Google |
| [0012](0012-server-rendered-ui-and-a-persons-decisions.md) | Server-rendered UI and a person's decisions | no JavaScript, writes only with ids and check codes |
| [0013](0013-ai-cost-model-for-the-hosted-app.md) | AI cost model for the hosted app | demand-driven shared batch, response cache, three budget layers |
| [0014](0014-daily-job-cache-budgets-demo-and-admin.md) | The daily job, cache, budgets, demo and admin | one run a day; read-only demo; admin without sandbox contents |
| [0015](0015-opentelemetry-without-leaking-data.md) | OpenTelemetry without leaking data | BCL in libraries, SDK in hosts, redaction before export |
| [0016](0016-azure-hosting-container-apps-shared-postgres-oidc.md) | Azure hosting | Container Apps + Job, shared PostgreSQL, Key Vault, OIDC deployment |
| [0017](0017-instant-ai-for-new-sandboxes-from-a-weekly-starter-set.md) | Instant AI for new sandboxes | a weekly starter set precomputed by the job, sign-in reads the cache |
| [0018](0018-ui-after-the-first-user-test.md) | The UI after the first user test | what AI added, undo of an AI match, a usable list, Czech |
