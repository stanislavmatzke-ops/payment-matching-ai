# Modernization: Delphi + PL/SQL → .NET

How the fictional legacy module FinPro PAYMATCH ([legacy/](../legacy)) is moved to .NET safely.
The approach: understand → pin down behaviour → port → strangle.

## 1. Understanding (phase 3)

_To do: an AI agent reads the legacy code and extracts the business rules into
`docs/business-rules.md`, each with a reference to the legacy source line. A human reviews them._

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

## 3. Port to .NET (phase 3)

_To do: rules rewritten as small classes with unit tests. The .NET version must produce the same
output as legacy on the golden master. Every deliberate difference (e.g. a fixed bug) is listed here._

## 4. Strangler fig (phase 4+)

_To do: rules stay as the first layer, AI is added only on top of them. Show that AI broke nothing
that worked._
