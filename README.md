# Payment matching with AI – from legacy to AI-native

> Work in progress. Built spec-first with an AI coding agent – see [AGENTS.md](AGENTS.md),
> [docs/SPEC.md](docs/SPEC.md) and [docs/PLAN.md](docs/PLAN.md).

A reference project that matches incoming bank payments to invoices. Deterministic rules (ported
from a fictional Delphi + PL/SQL system) handle the easy cases, an AI model handles the messy ones,
and anything uncertain goes to a human – because a wrong match is worse than no match.

## Quick start

Requirements: .NET 10 SDK, Docker.

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

2. Connect it. **Claude Code**, from the repository root:

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
| `tools/Match.Cli` | runs the whole pipeline (`stub`, `replay`, `record`, `live`, `rules`) |
| `evals/` | frozen gold set, recorded model answers and eval reports; runner in `tools/Eval.Cli` |
| `src/PaymentMatching.Mcp` | MCP server `payments-mcp`: read tools and proposals for a person (stdio / HTTP) |
| `docs/adr` | architecture decision records |

All data is synthetic. No real names, companies or accounts.

## License

[MIT](LICENSE)
