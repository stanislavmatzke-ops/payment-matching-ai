# ADR 0002: Deterministic synthetic data generator with ground truth

- Status: Accepted
- Date: 2026-11-12

## Context

Every later phase needs payment data: the legacy golden master (phase 2), characterization tests
(phase 3), the eval gold set (phase 5) and the daily job of the live app (phase 9). Real bank data
is off limits. Evals need to know the correct answer for each payment, including what must *not*
be matched.

## Decision

One generator library, `PaymentMatching.DataGen`, produces customers, invoices, a bank statement
and an `expected.csv` with the ground truth for each payment (case type, expected outcome,
invoice numbers, exchange rate, prompt-injection target).

- **Deterministic:** a seed drives `System.Random`; the generator never reads the clock (reference
  date is an option). The same seed gives byte-identical CSV, which CI checks.
- **Exact case mix:** weights become counts via the largest-remainder method, so ratios hold even
  for small sets and every case type is present.
- **One small builder per case type** (`Cases/*Case.cs`), so scenarios are easy to read and extend.
- **Clearly fictional data:** invented company word roots, fictional Demo Bank with unassigned bank
  code `9990`, but structurally valid IBANs (mod-97) and account numbers (mod-11) so validation
  logic is exercised realistically.
- **No third-party libraries** (no Bogus, no CsvHelper): the CSV writer is ~30 lines, and full
  control over the output keeps determinism and the "no real names" rule easy to guarantee.
- **Ground truth encodes the business rule "a wrong match is worse than no match":** partial
  payments and third-party payers expect `HumanReview`, unrelated payments and prompt injections
  expect `NoMatch`.

## Consequences

- The eval gold set (phase 5) can be generated, reviewed and frozen rather than hand-written.
- Changing a generator rule changes the sample data; the CI diff check makes that visible in a PR.
- The ground truth reflects the *intended* behaviour. Where legacy rules disagree with it (e.g.
  auto-matching a partial payment), that difference is documented in phase 3 rather than hidden.
