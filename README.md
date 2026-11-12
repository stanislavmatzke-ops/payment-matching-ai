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

## What is here so far

| Path | What |
|---|---|
| `src/PaymentMatching.DataGen` | deterministic synthetic data generator with ground truth |
| `tools/DataGen.Cli` | CLI for the generator |
| `data/sample` | 300 generated payments (seed 42) – see [docs/data-format.md](docs/data-format.md) |
| `docs/adr` | architecture decision records |

All data is synthetic. No real names, companies or accounts.

## License

[MIT](LICENSE)
