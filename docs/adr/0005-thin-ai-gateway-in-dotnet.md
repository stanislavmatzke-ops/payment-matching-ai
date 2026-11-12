# ADR 0005: A thin AI gateway in .NET (not Azure API Management, not LiteLLM)

- Status: Accepted
- Date: 2026-11-12

## Context

Layer 2 of the matcher calls a language model. Every call must have a timeout, retries, a budget,
token and cost records for the audit trail and the dashboard, and routing: a cheap model first,
a larger one only when the answer is not good enough. Tests and CI run without an API key
(ADR 0007). Later the provider may change (Claude on Azure AI Foundry, Azure OpenAI).

Options:

| Option | Good at | Cost for this project |
|---|---|---|
| **Azure API Management (AI gateway policies)** | central keys, quotas and token metrics for many apps and teams, managed identity, no code | an Azure resource and a deployment before the first test; routing on *answer quality* is not its job; nothing runs locally or in CI |
| **LiteLLM proxy** | one OpenAI-style API for 100+ providers, budgets, fallbacks | a Python service next to a .NET app, another container to run and secure; its fallback reacts to errors, not to our domain's confidence |
| **Thin gateway in our code** | routing on domain signals, record/replay in tests, cost per payment in our own audit log | ~200 lines we own and test |

## Decision

A thin gateway in `src/PaymentMatching.AiGateway`, about 200 lines:

- `ILlmProvider`: one structured-output call. `AnthropicProvider` uses the **official Anthropic
  C# SDK** (`Anthropic` on NuGet), which brings structured output (`output_config.format`),
  typed errors and retries with backoff for 429/5xx/network errors. A refusal or a truncated
  answer is an error, never a result.
- `AiGateway`: small model (`claude-haiku-5-5`, effort `low`) → large model (`claude-opus-5-5`,
  effort `medium`) when the answer is invalid, `uncertain` or below the confidence threshold. The
  caller supplies the parser and the confidence test, so the gateway knows nothing about payments.
  A transport failure does not escalate (the SDK already retried; a second model in an outage only
  adds cost). Per-call timeout and a call budget per run (the daily caps of phase 9 build on it).
- `PriceTable`: list prices with their date; every call becomes a `UsageRecord` (model, tier,
  outcome, tokens, cost, latency, raw output) that goes into the audit log.
- `RecordReplayProvider`: see ADR 0007.

## When the other options are the better choice

- **API Management** when many applications share models and keys, when a platform team owns
  quotas and chargeback, or when policy must be enforced outside the app. Our gateway would then
  call APIM instead of the model API and keep only the domain routing.
- **LiteLLM** when a team uses many providers from many languages and wants one protocol.

## Consequences

- Routing reacts to what matters here (a validated, confident answer), not only to HTTP errors.
- The provider can change behind `ILlmProvider` without touching the matching code.
- We own retries, budgets and price updates. Prices are in code and must be updated with `docs/cost.md`.
- One extra dependency (`Anthropic` SDK), only in the gateway project.
