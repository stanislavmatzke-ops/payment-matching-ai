# Legacy system: FinPro PAYMATCH (fictional)

> **This code is fiction.** It was written for this reference project to play the role of a
> mid-2000s payment matching module. It is not taken from any real system or employer.

The "old system" has two parts, as such systems often did:

| Part | Path | Status |
|---|---|---|
| Oracle PL/SQL nightly batch | [`plsql/pkg_payment_match.pkb`](plsql/pkg_payment_match.pkb) | source only, never executed |
| Delphi client module PAYMATCH | [`delphi/`](delphi) | **reference behaviour**: compiles with Free Pascal, runs as a CLI |

The Delphi module was (in the story) ported from the PL/SQL batch in 2005 and then patched over
the years. The two are *supposed* to implement the same rules. Whether they really do is a
question for the port ([docs/business-rules.md](../docs/business-rules.md)).

## Build and run

Requirements: Docker (Free Pascal runs in the `freepascal/fpc:3.2.2-full` image, nothing to install).

```bash
bash legacy/run-legacy.sh data/sample legacy/golden/seed-42/legacy_matches.csv
```

The script compiles `delphi/PayMatch.dpr` with `fpc -Mdelphi` and runs it.
- **Input:** a directory with `invoices.csv` and `bank_statement.csv` (format: [docs/data-format.md](../docs/data-format.md)).
  The module never reads `expected.csv`.
- **Output:** `payment_id;status;invoice_numbers;rule;amount_diff`

| `status` | Meaning |
|---|---|
| `P` | paired (matched) |
| `C` | partially paid, matched |
| `N` | not matched |
| `X` | skipped |

`rule` is `<how the invoice was found>/<outcome>`, e.g. `FIELD/EXACT` or `MSG/OVERPAID`.

## Delphi units

| Unit | Content | PL/SQL counterpart |
|---|---|---|
| `PayMatch.dpr` | CLI, CSV loading, result file, summary | `run_batch` |
| `uMatchEngine.pas` | `ProcessPayments` – the matching rules | `match_payment` |
| `uVsParser.pas` | `NormalizeVS`, `ExtractVSFromMessage` | `normalize_vs`, `parse_vs_from_message` |
| `uCsv.pas` | hand-written CSV split, amount formatting | – |
| `uGlobals.pas` | constants, record types, global arrays | package constants |

## Golden master

[`golden/`](golden) holds the legacy output for two generated datasets:

- `seed-42/legacy_matches.csv`: output for `data/sample` (300 payments),
- `seed-2005/`: 1 000 payments (`input/`) and their output.

CI rebuilds the legacy module and fails if the output changes. The .NET port (`src/PaymentMatching.Rules`) must
reproduce these files exactly before any rule is changed on purpose.
See [docs/modernization.md](../docs/modernization.md).
