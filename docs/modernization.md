# Modernization: Delphi + PL/SQL → .NET

How the fictional legacy module FinPro PAYMATCH ([legacy/](../legacy)) is moved to .NET safely.
The approach: understand → pin down behaviour → port → strangle.

## 1. Understanding (phase 3, done)

An AI agent read the legacy code and wrote [business-rules.md](business-rules.md): 24 rules, each
with a reference to the legacy source line, 15 differences between Delphi and PL/SQL, 13 risks
with concrete inputs and 12 open questions for the business.

To make this a fair test, the extraction ran in a **separate agent session without any context**.
It could read only `legacy/`, the data format and the repository conventions. It did not run the
code and did not see the golden master, this document or the list of rules planted in the
fictional legacy code. Then a human and the main agent checked the result:

- **All 13 planted rules were found**, including the deliberate bug and its knock-on effect.
- The agent also found **real issues nobody had planted on purpose**. For example, the PL/SQL
  version cuts `FAKTURA PRO FIRMU ABC 2026-1234` to invoice `2026-1`, and a missing currency is
  matched as CZK in PL/SQL.
- 17 sampled line references were all correct. One example used a real-sounding person name; it
  was replaced with a fictional one.
- **One rule was wrong by one:** the message scan window. The document said "first digit at offset
  0–14", the legacy code accepts offset 15. The characterization tests in step 3 caught it, and a
  probe run of the legacy binary confirmed it. The document was corrected.

Takeaway: the AI was more thorough than the author of the fiction, and its concrete examples
turned directly into unit tests. Line-level precision still needs a check against running code.

## 2. Characterization – golden master (phase 2, done)

The legacy module compiles with Free Pascal and runs as a CLI (ADR 0003). We ran it over two
generated datasets and committed its output as the golden master:

| Dataset | Payments | Output |
|---|---|---|
| `data/sample` (seed 42) | 300 | `legacy/golden/seed-42/legacy_matches.csv` |
| `legacy/golden/seed-2005/input` | 1 000 | `legacy/golden/seed-2005/legacy_matches.csv` |

CI rebuilds the legacy module on every push and fails if the output changes.

### Legacy baseline against the ground truth

Measured once against the generator's ground truth (`expected.csv`, which the legacy module never
reads). "Auto-applied" means status `P` or `C`.

| Metric | Seed 42 (300) | Seed 2005 (1 000) |
|---|---|---|
| Auto-applied | 197 (65.7 %) | 662 (66.2 %) |
| … correct and safe to automate | 160 | 530 |
| … correct invoice, but should have gone to a human | 34 | 113 |
| … **wrong invoice** | **3** | **19** |
| Precision of automatic matches | 81.2 % | 80.1 % |
| Matches following a prompt injection | 0 | 0 |

What the numbers show, by case type:

- **Exact VS + amount** works (443 of 450 in the larger set). The 7 misses are a side effect of
  wrong matches: an earlier payment was wrongly matched to the same invoice, and the correct
  payment is then rejected as a duplicate.
- **Wrong invoices** all come from VS typos. Some are found through a substring lookup and
  matched to a different invoice as a partial payment.
- **Partial payments and third-party payers** are matched automatically, without a human.
- **Foreign currency, multi-invoice payments and VS typos** are mostly left unmatched for manual work.
- **Prompt injection** does not work against legacy, but only by accident: the rules either cannot
  read the injected invoice number or the amount does not match.

This baseline is what the new system has to beat: precision ≥ 99 % at a higher automation rate.

## 3. Port to .NET (phase 3, done)

The rules live in [`src/PaymentMatching.Rules`](../src/PaymentMatching.Rules): small classes named
after the business rules (`VariableSymbol`, `MessageVsParser`, `InvoiceLookup`, `PaymentMatcher`,
`LegacyCsv`), 62 unit tests that follow the examples in business-rules.md, and 4 golden master
tests. `tools/Rules.Cli` runs the port like the legacy CLI. The port went in three steps (ADR 0004):

1. **Faithful port, bugs included.** The output on both golden datasets (1 300 payments) was
   **byte-identical with legacy on the first run**. Git tag `phase3-faithful-port`. The CSV files
   are read as Latin-1 so that byte-based legacy behaviour (a 16-byte message window, ASCII-only
   upper-casing) is reproduced exactly.
2. **Refactoring under the safety net:** `double` → `decimal` (F3). The golden master stayed
   byte-identical, so the change is proven safe.
3. **Deliberate changes.** From here the golden test does not require equality. It requires that
   **every line that differs from legacy is explained by a documented change**; an unexplained
   difference fails the build. The port output is committed next to the legacy output
   (`legacy/golden/*/port_matches.csv`), so any change is visible in review.

| ID | Change | Why | Effect on golden data |
|---|---|---|---|
| F1 | Substring ("fuzzy") VS lookup removed (BR-12) → `FIELD/NOT_FOUND` | matched wrong invoices and, by consuming them, blocked the correct later payments | 60 lines differ in the 1 000 set: fuzzy matches → not found, blocked payments → evaluated normally |
| F2 | Payment amount ≤ 0 (incl. unreadable) → `N`, `-/INVALID_AMOUNT`; invoices with amount ≤ 0 are never matched | 0 CZK used to close an invoice as a "partial payment" | none (generator never produces such amounts); unit tests |
| F3 | `decimal` instead of `double` | exact money arithmetic | none – proven by the golden master |

The classifier in the golden test needed two corrections, and both were real knock-on effects
of F1 that we had not foreseen. Once a fuzzy match no longer takes an invoice, a payment that legacy
rejected as `DUPLICATE` is evaluated normally. It can become `OVERPAID`, not only matched, and it
can come from a VS in the message, not only from the VS field.

### Port against the ground truth

| Metric | Legacy (300 / 1 000) | Port (300 / 1 000) |
|---|---|---|
| Auto-applied | 197 / 662 | 195 / 648 |
| … correct and safe to automate | 160 / 530 | 161 / 534 |
| … correct invoice, should have gone to a human | 34 / 113 | 34 / 114 |
| … **wrong invoice** | **3 / 19** | **0 / 0** |
| Exact VS + amount matched | 134 of 135 / 443 of 450 | **135 of 135 / 450 of 450** |
| Precision of automatic matches | 81.2 % / 80.1 % | 82.6 % / 82.4 % |

**No wrong invoice is matched any more.** All remaining imprecision is of one kind: partial
payments and third-party payers are still matched automatically, as in legacy, although a human
should confirm them. That is a business decision (open questions Q-02 and Q-07) and the job of the
next layer (phase 4): the rules propose, and routing sends uncertain cases to a person.

Still as in legacy, on purpose (decide with the business): tolerance `< 1.00 CZK` (Q-01), automatic
partial payments without a lower limit (Q-02), absolute 5 000 CZK advance overpayment (Q-04),
bank movements detected by name (Q-05), foreign currency left for manual work (Q-06), no payer
check (Q-07), invoice currency not checked (R-09).

## 4. Strangler fig: AI on top of the rules (phase 4, done)

The rules stay as layer 1, unchanged. Layer 2 handles only what they cannot decide (ADR 0006):

```
statement ─► layer 1: ported rules ─► policy (Q-02, Q-07) ─► auto match / person / no match
                                         │ rules could not decide
                                         ▼
             candidates chosen by code ─► AI gateway (small → large model) ─► validation by code
                                                                               ─► auto / person / no match
```

How we show that AI broke nothing that worked:

- Layer 1 inside the pipeline is **byte-identical with `port_matches.csv`** on both golden sets (test).
- AI never sees or changes a payment the rules decided (test: same decisions with and without AI).
- Two business decisions changed what "automatic" means: partial payments (Q-02) and third-party
  payers (Q-07) now go to a person. That is a deliberate change, not a side effect of AI.

### Results on `data/sample` (300 payments, real Claude answers, recorded)

| | Legacy | Port (phase 3) | Rules + AI (phase 4) |
|---|---|---|---|
| Matched without a person | 197 (65.7 %) | 195 (65.0 %) | **227 (75.7 %)** |
| … **wrong invoice** | 3 | 0 | **0** |
| … correct invoice, should have gone to a person | 34 | 34 | **0** |
| Precision of automatic matches | 81.2 % | 82.6 % | **100 %** |
| Prompt injections matched | 0 | 0 | 0 (all 15 flagged) |

The ceiling of this dataset is 76 %: the other 24 % should go to a person or stay unmatched, and
they do. By case: exact VS 135/135, VS in message 30/30, VS typo 23/24, multi-invoice 24/24, EUR
15/15 matched automatically; partial/overpayments 24/24 and third-party payers 18/18 to a person;
unrelated 15/15 and injections 15/15 not matched. The one VS typo not matched automatically went to
a person with the correct proposal: Claude Opus 5.5 was 0.88 confident, just under the 0.90 threshold.
A first recording run matched it (228); model answers vary between runs, which phase 5 must measure.

AI cost: 79 of 300 payments reached the model (the others were decided by rules or had no
candidate). 91 calls: 79 × Claude Haiku 5.5, 12 escalations to Claude Opus 5.5. **$0.19 for the
run, $0.64 per 1 000 payments**; latency per AI payment p50 2.0 s, p95 5.2 s. Opus makes up 84 % of
the cost, and 8 of its 12 calls were overpayments whose outcome is "a person" anyway. Not escalating
those is the first cost optimisation for phase 5 (needs an eval run).

An honest caveat: on synthetic data, the code (candidate selection + validation) does most of the
work. A deterministic stub that just takes the code's first suggestion reaches the same numbers. The
model's job here is to choose between prepared candidates, recognise injections and explain the
decision to the reviewer. Phase 5 evaluates it on harder cases and compares rules only, rules +
stub and rules + model.
