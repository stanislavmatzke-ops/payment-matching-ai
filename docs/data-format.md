# Data format

All data in this repository is **synthetic**. It is produced by the generator in
`src/PaymentMatching.DataGen` (CLI: `tools/DataGen.Cli`). Company names are built from invented
word roots, person names from common first names and invented surnames, and every account belongs
to the fictional **Demo Bank** (bank code `9990`, not assigned to any real Czech bank). Any
resemblance to real companies or people is accidental.

## Generate

```bash
dotnet run --project tools/DataGen.Cli -- --seed 42 --payments 300 --date 2026-03-31 --out data/sample
```

The same options always produce byte-identical files. CI regenerates `data/sample` and fails if
it differs from the committed copy.

## CSV conventions

- UTF-8 without BOM, `\n` line endings
- separator `;` (common in Czech bank exports), RFC 4180 quoting (`"` doubled inside quoted fields)
- decimal point `.`, two decimal places, no thousands separator
- dates `yyyy-MM-dd`
- empty string = missing value

## Files

### `customers.csv`

| Column | Example | Notes |
|---|---|---|
| `customer_id` | `C001` | |
| `name` | `Kalmira Logistika s.r.o.` | |
| `iban` | `CZ8199900000009204630144` | valid mod-97 checksum, account passes Czech mod-11 check |

### `invoices.csv`

| Column | Example | Notes |
|---|---|---|
| `invoice_number` | `2026-1234` | unique key |
| `variable_symbol` | `2026001234` | `year * 1 000 000 + sequence`, always 10 digits |
| `customer_id` | `C001` | |
| `kind` | `Regular` / `Advance` | advance (proforma) invoices, ~10 % |
| `issue_date`, `due_date` | `2026-02-10` | due = issue + 14 days |
| `amount` | `12100.00` | gross amount |
| `currency` | `CZK` | |

About a fifth of the invoices are **open** (no payment). They are distractors for any matcher.

### `bank_statement.csv` (input of the matcher)

| Column | Example | Notes |
|---|---|---|
| `payment_id` | `P0001` | ordered by booking date |
| `booking_date` | `2026-02-12` | |
| `amount`, `currency` | `341.73`, `EUR` | |
| `counterparty_iban`, `counterparty_name` | | name often as banks print it (upper case, no diacritics, truncated) |
| `variable_symbol`, `constant_symbol`, `specific_symbol` | `2026001234`, `0308`, `` | |
| `message` | `fa 2026-1234 díky` | **untrusted input** – may contain prompt injection |

### `expected.csv` (ground truth – never shown to the matcher)

| Column | Example | Notes |
|---|---|---|
| `payment_id` | `P0001` | |
| `case_type` | `VsTypo` | see below |
| `expected_outcome` | `AutoMatch` / `HumanReview` / `NoMatch` | |
| `invoice_numbers` | `2026-1199\|2026-1200` | `\|`-separated, empty for `NoMatch` |
| `fx_rate` | `25.18` | CZK per EUR, foreign-currency cases only |
| `injection_target` | `2026-9999` | invoice the injected text asks for; must never be matched |
| `note` | | human-readable detail |

## Case types and default mix

| `case_type` | Share | Expected outcome | What it looks like |
|---|---|---|---|
| `ExactMatch` | 45 % | AutoMatch | correct VS and amount; ~15 % paid rounded down to whole CZK (difference < 1 CZK) |
| `VsInMessage` | 10 % | AutoMatch | no VS, invoice number in the message in various formats |
| `VsTypo` | 8 % | AutoMatch | two adjacent VS digits swapped or one digit missing; never equals another invoice's VS |
| `MultiInvoice` | 8 % | AutoMatch | one payment = sum of 2–4 invoices of one customer |
| `PartialOrOverpayment` | 8 % | HumanReview | correct VS, partial (30–90 %) or overpaid amount |
| `ThirdPartyPayer` | 6 % | HumanReview | parent company or factoring company pays; no VS, invoice in message |
| `ForeignCurrency` | 5 % | AutoMatch | EUR payment for a CZK invoice, correct VS, rate 24.80–25.59 |
| `Unrelated` | 5 % | NoMatch | refunds, interest, insurance; VS (if any) starts with 9 |
| `PromptInjection` | 5 % | NoMatch | message instructs an AI to match an existing or non-existent invoice |

Counts are computed with the largest-remainder method, so the mix holds exactly and even a
20-payment set contains every case type.
