# ADR 0003: Legacy module runs in Free Pascal (Docker); its output is the golden master

- Status: Accepted
- Date: 2026-11-12

## Context

The modernization story needs a legacy system that can actually run. Only then can its behaviour
be captured and compared with the .NET port. The legacy code is Delphi (Object Pascal) plus an
Oracle PL/SQL package. Delphi itself is commercial and Windows-only, and Oracle is heavy for CI.

## Decision

- **Delphi code compiles with Free Pascal** in Delphi mode (`fpc -Mdelphi`), and the compiler runs
  in the official `freepascal/fpc:3.2.2-full` Docker image. The same compiler builds it locally
  (any OS with Docker) and in CI, with nothing to install.
- **The Delphi CLI is the reference behaviour.** The PL/SQL package is kept as source only. It
  documents where the logic came from but is not executed.
- **Golden master:** the legacy CLI runs over two generated datasets (300 and 1 000 payments).
  Its output is committed under `legacy/golden/`. A CI job rebuilds the module and fails if the
  output changes.
- The legacy CLI reads the same CSV files the generator produces (ADR 0002). It never reads the
  ground truth.

## Consequences

- Phase 3 gets a precise, versioned target: the .NET port must reproduce `legacy/golden/` byte by
  byte (characterization tests) before any deliberate change.
- Any intentional difference from legacy (e.g. a fixed bug) shows up as a diff against the golden
  master and must be documented.
- Docker becomes a prerequisite for working on the legacy part. The .NET build and tests do not
  need it.
- Free Pascal is not Delphi. Minor differences (e.g. `DefaultFormatSettings`) are handled with
  `{$IFDEF FPC}` the way real cross-compiler code did.
