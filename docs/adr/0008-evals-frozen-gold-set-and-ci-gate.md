# ADR 0008: Evals on a frozen gold set, with a gate in CI

- Status: Accepted
- Date: 2026-11-12

## Context

Unit tests show that each piece works; they do not show how well the matcher as a whole decides,
whether a prompt or model change made it better or worse, or what it costs. The business rule is
"a wrong match is worse than no match", so the main question is the precision of automatic matches.

Phase 4 measured on `data/sample` and found that a code-only stub reaches the same numbers as the
model: on that data the eval could not tell the configurations apart. An eval set must contain
cases where trusting the code's hints and reading the payment lead to different decisions.

## Decision

**Gold set** `evals/dataset/gold-v1`: 300 synthetic payments from the generator (`--profile eval`,
seed 5005), 70 % of the usual case mix and 30 % hard cases (ties, invoice numbers the code cannot
parse, injections without trigger words). Ground truth is the business truth, not what the system
can do. Reviewed (`REVIEW.md`), then **frozen**: `MANIFEST.sha256` pins the files, a test and the
runner refuse a changed file, and a correction is a new version (`gold-v2`). The generator can keep
evolving without moving the yardstick.

**Runner** `tools/Eval.Cli` over `src/PaymentMatching.Evals`. It runs the full pipeline per
configuration with a fresh in-memory decision log and scores the decisions:

| Configuration | What answers layer 2 |
|---|---|
| `rules` | nothing (AI off) |
| `stub` | the code's first suggestion, no model |
| `small` | the small model alone |
| `routing-v1` | small → large on low confidence (phase 4) |
| `routing` | small → large only when an automatic match is still possible (ADR 0009) |

Metrics: precision of automatic matches (main), automation and its ceiling, false match rate by
case type, injection defence (not matched **and** target not proposed), accuracy of the proposal a
person gets, cost per 1 000 payments, calls and escalations, latency p50/p95, confidence calibration,
a free threshold sweep over the same recorded answers and, in live mode, run-to-run variance.
Report: `evals/reports/<date>-<models>-<prompt>.md`.

**Modes**: `replay` (recorded answers, CI), `record` (fetch and save missing answers),
`live` (nothing saved; `--repeat n` for variance). Record and live need a key and cost money; they
are run by hand.

**Gate** (CI fails, exit 1):

- every configuration, the stub included: precision ≥ 99 % and no injection matched. Safety must
  come from validation, not from the model, so a configuration without a model must pass too;
- `routing` (production): additionally no injection target shown to a person as the proposal.

## Consequences

- A prompt, model, threshold or validation change shows its effect in a pull request, for free.
- The first eval run already paid off: the stub matched all 30 ties automatically (8 to the wrong
  invoice, precision 84 %). Validation now rejects a proposal when another candidate set passes the
  same checks (`AMBIGUOUS`), and the exhaustive validation test also runs over the gold set.
- 300 synthetic payments are a small, clean sample: thresholds tuned on them can overfit. Changes
  are made only with a margin, and the live feedback of phase 8 is the check against reality.
- Recordings for the gold set are part of the repo (a few hundred small JSON files).
