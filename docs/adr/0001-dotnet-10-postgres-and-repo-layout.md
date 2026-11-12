# ADR 0001: .NET 10, PostgreSQL and repository layout

- Status: Accepted
- Date: 2026-11-12

## Context

The project shows how a legacy matching module (Delphi + PL/SQL) moves to a modern stack with an
AI layer on top. The stack must be mainstream in enterprise environments, run locally with one
command, and deploy cheaply to Azure later (phase 11).

## Decision

- **.NET 10 (LTS)**, C# with nullable reference types, warnings as errors and code-style
  enforcement in the build. The SDK is pinned in `global.json` (feature band roll-forward).
- **PostgreSQL 17** in Docker Compose locally; Azure Database for PostgreSQL Flexible Server later.
- **Central package management** (`Directory.Packages.props`) so every dependency and its version
  is visible in one file.
- **Tests:** xUnit v3 on Microsoft.Testing.Platform (the runner `dotnet test` uses on .NET 10).
- **Layout:**
  - `src/` – production code (one project per bounded piece, starting with `PaymentMatching.DataGen`)
  - `tools/` – developer CLIs (`DataGen.Cli`)
  - `tests/` – one test project per production project
  - `legacy/` – the fictional legacy system (phase 2)
  - `evals/`, `prompts/` – AI evaluation and versioned prompts (phases 4–5)
  - `docs/` – SPEC, PLAN, ADRs and supporting documents
- **LF line endings** everywhere (`.gitattributes`, `.editorconfig`), so `dotnet format` gives the
  same result on Windows and on the Linux CI runner.

## Consequences

- CI needs only the .NET SDK and runs without any API key or database.
- Adding a project means adding it to `PaymentMatching.slnx`; shared build settings come for free.
- Choosing Postgres over SQL Server keeps local and Azure costs low; EF Core vs. Dapper is decided
  when the first persistence code appears.
