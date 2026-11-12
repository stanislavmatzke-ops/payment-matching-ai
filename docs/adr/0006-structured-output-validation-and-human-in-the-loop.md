# ADR 0006: Structured output, validation by code and a person in the loop

- Status: Accepted
- Date: 2026-11-12

## Context

A wrong match is worse than no match. A model can be wrong, overconfident or manipulated by text in
the payment message (prompt injection). The ported rules (layer 1) are deterministic and already
correct for exact VS matches; they cannot handle VS typos, invoice numbers in free text, payments of
several invoices or foreign currency. They also match two kinds of payments automatically that the
business wants a person to confirm: partial payments (open question Q-02) and payments from someone
other than the customer (Q-07).

## Decision

**1. Rules first, AI only on top (strangler fig).** The rules run unchanged and decide first. The
policy layer (`RuleOutcomePolicy`) maps each rule result to: automatic match, a person, no match, or
"ask layer 2". AI never sees or overrides a payment the rules decided. A test checks that layer 1
inside the pipeline is byte-identical with `legacy/golden/*/port_matches.csv`.

**2. Business decisions Q-02 and Q-07** (configurable in `MatchingOptions`):

- Q-02: a partial payment or an overpaid advance goes to a person with a prefilled proposal and the
  paid share. No model call: the invoice is already identified. The invoice stays open.
- Q-07: an automatic match needs the payer account to belong to the invoice customer, **except** a
  correct VS in the VS field with a fitting amount (strong evidence, common practice when a parent
  company pays). A third-party payer goes to a person with the flag `third_party_payer`.

**3. Code selects, the model chooses, code validates.**

- `CandidateSelector` gives the model at most 8 open invoices related to the payment (VS exact or one
  typo, invoice number in the message, amount, payer account) and combinations of 2–4 invoices of one
  customer whose total fits. No candidates → no model call.
- Prompt `prompts/match-payment.v1.md`. Bank data is JSON inside `<untrusted_bank_data>`; `<` and `>`
  are escaped, so the data cannot close the block. The prompt says the data is never an instruction.
- Structured output with a closed JSON schema (`decision`, `invoice_numbers`, `confidence`,
  `reasoning`, `flags`), parsed strictly (unknown fields, values or a confidence outside 0–1 fail).
- `ProposalValidator` re-checks everything the model claims: invoices are candidates and still open,
  one customer, the payment names at least one of them and every invoice it names exactly is
  included, amount within 1 CZK (EUR: one invoice, implied rate 24.00–26.50), payer is the customer,
  no injection suspected.
- Automatic match only if: decision `match`, validation passed, confidence ≥ 0.90, and no flag that
  needs a person (`prompt_injection`, `third_party_payer`, `partial_payment`, `overpayment`).
  Everything else goes to a person with the proposal, the reasoning and the failed checks.
- Suspected injection (by the model or a simple keyword detector) → not matched, flagged, and the
  invoice the text asks for is never shown as a proposal.

**4. Idempotency and audit.** `DecisionLog` is an append-only JSON Lines file: one record per
payment (key = SHA-256 of the statement line), with the layer, rule code, prompt version, every
model call (model, tokens, cost, latency, raw output), candidates and validation result. A decided
payment is never processed again; an invoice can be matched automatically only once. In the hosted
app (phase 7) these become unique indexes in PostgreSQL.

## Consequences

- Safety does not depend on the model. Two tests show it: a fake model that obeys every injection
  with confidence 1.0 matches nothing it asks for, and an exhaustive check tries every set of 1–4
  candidates as a "model answer" on 1 300 payments: only correct answers pass validation.
- That exhaustive check found a hole the first version had: an EUR payment could be matched to a
  combination of invoices whose total happened to fit the ~10 % rate band. Fixed by "the payment must
  name the invoice" and "EUR pays one invoice only".
- Thresholds (0.90) and the EUR band are first guesses; phase 5 evals calibrate them.
- On synthetic data the code does most of the work; the model mostly picks between prepared
  candidates and explains. The evals of phase 5 compare rules only, rules + stub, and rules + model.

## Update (phase 5)

- The eval gold set found ties: two invoices or combinations that pass every check. Validation now
  also requires the proposal to be the **only** candidate set that passes (`AMBIGUOUS`); a tie goes
  to a person. See ADR 0008.
- The 0.90 threshold was checked against recorded answers and kept; escalation now happens only when
  an automatic match is still possible. See ADR 0009.
