# ADR 0009: Escalate only when it can change the outcome; keep the 0.90 threshold

- Status: Accepted
- Date: 2026-11-12

## Context

Phase 4 escalated from the small model (Claude Haiku 5.5) to the large one (Claude Opus 5.5)
whenever the small answer was `uncertain` or below 0.90 confidence. On `data/sample` the large model
was 84 % of the cost, and 8 of its 12 calls were overpayments, which go to a person whatever any
model says. Phase 4 also left two questions: is the 0.90 threshold right (one correct answer at 0.88
went to a person), and how much do answers vary between runs (228 vs 227 automatic matches)?

## Decision

**Escalation rule.** Before calling the model, code checks whether any candidate invoice or suggested
combination could pass validation (`ProposalValidator.AnyValidProposal`). If none can (overpayment,
third-party payer, an amount that fits nothing, a tie, an invoice the payment does not name in a form
the code can verify), the outcome is "a person" whatever the model answers, so the small model's
proposal and reasoning are enough and the large model is not asked. Otherwise escalation works as
before. `MatchingOptions.EscalateOnlyWhenAutoMatchPossible`, on by default.

Measured on the gold set (300 payments, 136 reach the model; recorded real answers, report
`evals/reports/2026-11-12-claude-haiku-5-5+opus-5-5-match-payment.v1.md`):

| | Phase 4 rule (`routing-v1`) | New rule (`routing`) | Small model only |
|---|---|---|---|
| Precision of automatic matches | 100 % | 100 % | 100 % |
| Automation | 53.3 % | 53.3 % | 52.0 % |
| Large-model calls | 39 | 4 | 0 |
| Cost per 1 000 payments | $2.21 | **$0.36** | $0.17 |
| Latency p95 per AI payment | 7.9 s | 4.1 s | 3.5 s |

The small model alone sends 4 VS typos to a person that the large model then matches correctly, so
escalation stays, but only where it can matter.

**Threshold.** The automatic-match threshold stays at **0.90**. Every model answer that passed
validation on the gold set had confidence ≥ 0.90 and was right; a sweep over the same recorded
answers shows no change between 0.80 and 0.90 and a loss of automation at 0.95 (53.3 → 44.7 %)
without any gain in precision. Lowering it would rest on no evidence from this data (the 0.88
answer of phase 4 does not occur here), and on 300 synthetic payments it would be overfitting.

On `data/sample` the new rule keeps all 227 automatic matches and cuts the large-model calls from
12 to 4: **$0.64 → $0.27 per 1 000 payments**.

**Variance.** Three live runs of `routing` on the gold set (nothing saved; report
`evals/reports/2026-11-12-routing-variance-live-3-runs.md`): 160 automatic matches and 100 %
precision in every run, 1–4 large-model calls, $0.20–0.34 per 1 000 payments. 8 payments got a
different proposal between runs, all of them ties (`AmbiguousReference`): the model picks one of two
equally fitting combinations for the person, and the outcome (a person decides) never changed. The
phase 4 difference (228 vs 227) was the same kind of thing at the threshold edge. Model variance
shows up as work for a person, not as a wrong match, because validation decides.

## Consequences

- About 6× lower model cost on the gold set with the same decisions; the large model is reserved for
  payments where its answer can turn "a person" into a correct automatic match.
- The rule is code, not a model judgement, and uses the same validator that decides the match, so it
  cannot drift from it. A tie or an unverifiable reference never reaches the large model.
- A payment the code cannot verify (e.g. an invoice number in an unusual format) gets no second
  opinion; it goes to a person with the small model's proposal. That is the intended trade-off.
- The threshold is revisited with the live feedback of phase 8, not with more synthetic data.
