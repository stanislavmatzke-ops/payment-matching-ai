# ADR 0015: OpenTelemetry – model, tokens and cost in traces, without leaking data

- Status: Accepted
- Date: 2026-11-12

## Context

The audit log already keeps every model call (`UsageRecord`: model, tier, tokens, cost, latency, cached)
and `daily_usage` keeps the daily sums. What was missing is the operator's view: one trace that shows
*payment → layer → model call → outcome*, metrics for spend and budget hits, and the same data in
Application Insights once the app runs in Azure (phase 11). SPEC asks for OpenTelemetry with model,
tokens, cost and latency, locally the Aspire dashboard or Jaeger.

Constraints: CI and tests run without a telemetry backend; bank statement text is untrusted input and,
with names, e-mails and tenant ids, must never leave the app through telemetry – a trace store is
usually read by more people and kept longer than the database.

## Decision

- **Libraries record on the BCL only.** `AiGateway`, `Matching` and the job use one `ActivitySource` and
  one `Meter` named `PaymentMatching` (`AiGateway/Telemetry.cs`). No OpenTelemetry package in them;
  without a listener the cost is close to zero.
- **Spans:** `matching.run` → `matching.payment` (synthetic payment id, layer, rule code, outcome, decided
  by, flag and failure codes, number of candidates, deferred) → `ai.complete` (final model, escalated,
  budget exhausted, cost) → `chat {model}` per provider call with the GenAI semantic conventions
  (`gen_ai.request.model`, `gen_ai.usage.input_tokens`/`output_tokens`) plus tier, outcome, cost,
  cached and the provider-reported latency (replayed calls are instant, so span duration is not it).
  The job adds `job.run` and `job.sandbox` with a 12-hex SHA-256 **pseudonym** of the tenant id.
- **Metrics:** `payments.ai.calls` (model, tier, outcome, cached), `payments.ai.tokens`,
  `payments.ai.cost` (USD), `payments.ai.call.duration`, `payments.ai.budget_exhausted`,
  `payments.matching.decisions` (layer, outcome, decided by). Traces explain one payment; metrics give
  cheap totals for spend and budget charts.
- **Hosts only start the SDK** (`src/PaymentMatching.Observability`, used by the web app, job, MCP server
  and Match.Cli), and only when configured: `OTEL_EXPORTER_OTLP_ENDPOINT` → OTLP (locally the Aspire
  dashboard from `docker compose --profile observability`), `APPLICATIONINSIGHTS_CONNECTION_STRING` →
  Azure Monitor exporter. Nothing set = no SDK, nothing exported. Logs stay on stdout (not exported).
- **What never goes into telemetry:** payment message, payer name, IBAN and variable symbol, customer
  names, people's names and e-mails, prompts, model output and reasoning, error and exception messages,
  query strings, the tenant id in clear. Our spans carry only codes, numbers, model names and synthetic
  ids; failures carry a category (`timeout`, `transport`, `answer`), never the text.
- **Redaction processor before every exporter** for spans we do not own: it drops `url.query`, user
  agent, client address, SQL text and exception tags, cuts the query from `url.full`, and removes the
  status description of foreign spans. This was needed in practice: the MCP SDK puts a failed tool's
  answer – which echoes the agent's arguments – into the span status.
- **Npgsql spans are left out** (its metrics are in): a database error carries the offending values in
  its message, and SQL text adds little to this app's traces.
- **Tests without a collector:** a BCL `ActivityListener`/`MeterListener` capture filtered by the test's
  own trace (tests run in parallel), and the SDK's in-memory exporter for the web and MCP pipelines.
  Leak tests replay the recorded real-model answers over `data/sample` (injections included), run the
  job over a sandbox and call MCP tools with marked arguments, then assert that no tag contains any of
  that text and that our tags are on an allow-list. A new tag is a deliberate change of that list.

## Alternatives

- **Jaeger** instead of the Aspire dashboard: traces only. Aspire shows traces and metrics from one
  container with OTLP built in.
- **Azure Monitor OpenTelemetry distro** (`Azure.Monitor.OpenTelemetry.AspNetCore`): one line, but it
  also turns on logs, live metrics and its own instrumentations; the plain exporter keeps the same
  pipeline (and the same redaction) for OTLP and Application Insights.
- **Sending prompts and completions as GenAI events** (the semantic conventions allow it): useful for
  debugging a model, but here the prompt is bank statement text. The audit log in the tenant's own
  database is the place for raw output, behind tenant isolation.

## Consequences

- One trace shows why a payment was matched or not and what it cost; App Insights gets the same data in
  phase 11 by setting one connection string.
- Four new packages, all in the Observability project or the web app (SDK, OTLP, Azure Monitor exporter,
  ASP.NET Core instrumentation) and one test package (in-memory exporter).
- Sampling is always-on: traffic is tiny. If it grows, a ratio sampler in `TelemetrySetup` limits
  Application Insights ingestion.
- Replay runs report the recorded cost and latency in telemetry, as in the audit log: they describe the
  recorded call, not money spent now. `payments.ai.cached` marks answers reused from the response cache.
