# ADR 0007: Record/replay for model calls in tests and CI

- Status: Accepted
- Date: 2026-11-12

## Context

CI must pass without an API key (AGENTS.md), be deterministic and cost nothing. Yet the tests should
exercise real model answers, not only hand-written fakes, and a prompt change must be visible in a
pull request.

## Decision

`RecordReplayProvider` wraps any provider:

- The key is a SHA-256 of the whole request: model, effort, max tokens, system prompt, JSON schema
  and user content. A new prompt version, another model or changed input data never replays an old
  answer.
- **Replay** (tests, CI): only recorded answers; a missing recording is an error, never a live call.
- **Record** (by hand, with a key): reuse recorded answers, fetch missing ones and save them.
- Recordings are small JSON files in `evals/recordings/` (request content, model, raw answer,
  tokens, latency), committed to git and readable in review.
- Unit tests use fakes (an obedient model, a stub that follows the code's hints); one end-to-end
  test replays the recorded real answers for `data/sample`.

Command: `dotnet run --project tools/Match.Cli -- data/sample --mode record` (needs
`dotnet user-secrets set "Anthropic:ApiKey" "<key>" --id payment-matching-ai`).

## Consequences

- CI runs the real answers of the recorded model for free and without secrets.
- Changing the prompt, the candidate format or the data invalidates recordings: the replay test
  fails until someone records again. This is intended: a prompt change needs a new eval run.
- Replay shows recorded latency and cost, so reports from CI still have realistic numbers.
- Recordings contain only synthetic data (no secrets: the API key is never part of a request we store).
