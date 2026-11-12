# Business rules of the legacy PAYMATCH module

This document captures the behaviour of the fictional legacy module **FinPro PAYMATCH**
(`legacy/delphi`, Delphi / Free Pascal) and its predecessor, the Oracle batch
`pkg_payment_match` (`legacy/plsql`). It was written from the source code only. Nothing was
executed and no recorded output was consulted. The goal is a specification precise enough to
port the module to .NET with **byte-identical output**.

The Delphi module is the reference behaviour (`legacy/README.md`). Where the PL/SQL package
differs, the difference is listed in [section 3](#3-delphi-vs-plsql).

Business context: incoming payments from a bank statement are matched to issued invoices.
**A wrong match is worse than no match.**

Conventions used below:

- `diff` = payment amount − invoice amount (negative = underpaid, positive = overpaid).
- "VS" = variable symbol. "How" = the first part of the rule code (`FIELD`, `MSG`, `FUZZY`, `-`).
- Line references point to the file and line as they are in the repository at the time of writing.
- Offsets inside strings are 0-based, Pascal string indexes are 1-based.

---

## 1. Summary

| ID | Name | Type | Risk of wrong match |
|---|---|---|---|
| BR-01 | Invoice file loading | technical limitation | medium |
| BR-02 | Payment file loading (rows can silently disappear) | technical limitation | medium |
| BR-03 | CSV line splitting | technical limitation | low |
| BR-04 | Amount parsing (invalid amount becomes 0) | suspected bug | **high** |
| BR-05 | Processing order and in-run "paid" state | business rule | medium |
| BR-06 | Bank's own movements are skipped (name contains `DEMO BANK`) | business rule | medium |
| BR-07 | Foreign currency payments are not matched | business rule | low |
| BR-08 | VS normalisation | business rule | low |
| BR-09 | VS source: field first, message only when field is empty | business rule | medium |
| BR-10 | VS extraction from the payment message | business rule | **high** |
| BR-11 | Exact invoice lookup by VS (first invoice wins) | business rule | medium |
| BR-12 | Fuzzy (substring) invoice lookup | business rule / suspected bug | **high** |
| BR-13 | Invoice not found | business rule | low |
| BR-14 | Invoice already paid in this run → duplicate | business rule | medium |
| BR-15 | Amount tolerance (< 1.00 CZK, both directions) | business rule | medium |
| BR-16 | Underpayment: partial match only when VS came from the field | business rule | **high** |
| BR-17 | Advance invoice: overpayment up to 5 000 CZK is accepted | business rule | medium |
| BR-18 | Any other overpayment is rejected | business rule | low |
| BR-19 | Matched and partially paid invoices are consumed | business rule / suspected bug | medium |
| BR-20 | Rule code composition | business rule (output contract) | low |
| BR-21 | `amount_diff` presence and formatting | technical (output contract) | low |
| BR-22 | Result file writing | technical (output contract) | low |
| BR-23 | Input fields that are read but never used | technical limitation | medium |
| BR-24 | Console summary, arguments and exit codes | technical | low |

24 rules in total: 4 high risk, 11 medium risk, 9 low risk.

---

## 2. Rules

### BR-01 Invoice file loading

- **Source:** `legacy/delphi/PayMatch.dpr:17-60`, CSV split `legacy/delphi/uCsv.pas:21-60`.
  PL/SQL: none (reads table `invoice`).
- **Type:** technical limitation. **Risk:** medium.

Behaviour:

1. The file is `<input-dir>/invoices.csv` (`PayMatch.dpr:160`). The directory gets a trailing
   path delimiter (`PayMatch.dpr:158`). The file name is case-sensitive on Linux.
2. The **first line is always skipped** as a header, whatever it contains (`PayMatch.dpr:32-36`).
   Column names are not checked; columns are taken **by position**.
3. Lines that are empty after `Trim` (spaces and control characters) are skipped
   (`PayMatch.dpr:37-38`).
4. A line split into **fewer than 8 fields is skipped silently** (`PayMatch.dpr:40-41`). More than
   8 fields is accepted, extra fields are ignored.
5. Field mapping (`PayMatch.dpr:46-52`):

   | Index | CSV column | Stored as | Processing |
   |---|---|---|---|
   | 0 | `invoice_number` | `Number` | as is, not trimmed |
   | 1 | `variable_symbol` | `VS` | `NormalizeVS` (BR-08), at load time (`PayMatch.dpr:47`) |
   | 2 | `customer_id` | `CustomerId` | never used |
   | 3 | `kind` | `Kind` | as is, not trimmed, compared case-sensitively with `Advance` |
   | 4, 5 | `issue_date`, `due_date` | – | not read |
   | 6 | `amount` | `Amount` (Double) | `StrToAmt` (BR-04) |
   | 7 | `currency` | `Currency` | stored, **never used** |

6. `Paid` starts as `False` for every invoice (`PayMatch.dpr:52`). There is no input that says an
   invoice was paid before this run.
7. A missing file raises an unhandled I/O runtime error (no `try/except`), the program stops.

Port note: an invoice dropped by rule 4 is invisible. Payments for it end as `NOT_FOUND`
without any warning.

### BR-02 Payment file loading

- **Source:** `legacy/delphi/PayMatch.dpr:62-107`. PL/SQL: none (reads table `bank_statement`).
- **Type:** technical limitation. **Risk:** medium.

Behaviour is the same as BR-01 (header skipped, blank lines skipped), except:

1. File is `<input-dir>/bank_statement.csv` (`PayMatch.dpr:161`).
2. A line with **fewer than 10 fields is skipped silently** (`PayMatch.dpr:85-86`). Such a payment
   is **missing from the output file**, so nobody ever reviews it.
3. Field mapping (`PayMatch.dpr:91-99`):

   | Index | CSV column | Stored as | Used? |
   |---|---|---|---|
   | 0 | `payment_id` | `Id` | yes, copied to output unchanged |
   | 1 | `booking_date` | – | not read |
   | 2 | `amount` | `Amount` (Double, BR-04) | yes |
   | 3 | `currency` | `Currency` | yes (BR-07) |
   | 4 | `counterparty_iban` | `Iban` | **never used** |
   | 5 | `counterparty_name` | `Name` | yes (BR-06) |
   | 6 | `variable_symbol` | `PayVS` | yes (BR-09) |
   | 7 | `constant_symbol` | `KS` | **never used** |
   | 8 | `specific_symbol` | `SS` | **never used** |
   | 9 | `message` | `Msg` | yes (BR-10) |

4. Fields after index 9 are ignored. If the message contains an unquoted `;`, only the text before
   it is the message.
5. Duplicate `payment_id` values are not detected; each row is processed and written.

### BR-03 CSV line splitting

- **Source:** `legacy/delphi/uCsv.pas:21-60`. PL/SQL: none.
- **Type:** technical limitation. **Risk:** low.

`SplitLine` is a hand-written state machine over one physical line:

1. Separator is `;` (`uCsv.pas:50-54`). The last field is always added, so a line with *n*
   separators gives *n + 1* fields (`uCsv.pas:59`). An empty line would give 1 field.
2. A `"` **anywhere** outside quotes switches into quoted mode (`uCsv.pas:48-49`), not only at
   the start of a field. The quote itself is dropped. `ab"c;d"e` gives one field `abc;de`.
3. Inside quotes, `""` gives one `"` (`uCsv.pas:37-41`); a single `"` ends quoted mode
   (`uCsv.pas:42-43`). Text after a closing quote is appended to the same field.
4. Quoted fields **spanning several lines are not supported** (`uCsv.pas:3-4`). An unclosed quote
   swallows all following separators up to the end of the line. The line then usually has too few
   fields and is dropped (BR-01/BR-02). The continuation line is read as a new record.
5. No trimming of fields.
6. Strings are byte strings (FPC `-Mdelphi`: `string` = `AnsiString`). UTF-8 is passed through
   byte for byte, `Length` counts bytes.

Line endings: lines are read with `ReadLn`. Even if a `\r` stayed at the end of a line, it would
only end up in the last field (invoice `currency`, unused; payment `message`, where it has no
effect). A UTF-8 BOM would only be in the skipped header line.

### BR-04 Amount parsing

- **Source:** `legacy/delphi/uCsv.pas:62-66`, format settings `legacy/delphi/uCsv.pas:81-87`.
  PL/SQL: amounts are `NUMBER` columns, no parsing.
- **Type:** suspected bug. **Risk:** **high**.

`StrToAmt(S) = StrToFloatDef(Trim(S), 0, g_FS)` with decimal separator `.`.

- Any value that cannot be parsed gives **0**, silently (the code has
  `// TODO: report bad amounts instead of zero`, `uCsv.pas:64`). Examples: empty string,
  `12100,00` (decimal comma), `12 100.00` (space as thousands separator), `CZK 100`.
- Negative values are accepted (`-500.00`).
- The result is an IEEE 754 `Double`. All later arithmetic (BR-15 to BR-18) is in `Double`.

Consequences (see section 4): a payment with an unreadable amount and a VS in the field becomes a
**partial payment** (`C`) and consumes the invoice. An invoice with an unreadable amount
(0) is "exactly paid" by any payment below 1.00 CZK.

### BR-05 Processing order and in-run "paid" state

- **Source:** `legacy/delphi/uMatchEngine.pas:21-23`, `legacy/delphi/uMatchEngine.pas:88-92`,
  `legacy/delphi/uMatchEngine.pas:126-130`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:133-142`.
- **Type:** business rule. **Risk:** medium.

1. Payments are processed **one by one in file order** (`uMatchEngine.pas:21`). No sorting.
2. Each invoice has an in-memory `Paid` flag, set when a payment is matched (`P`) or partially
   matched (`C`) to it (BR-19). A later payment that finds the same invoice gets `DUPLICATE` (BR-14).
3. So the result **depends on order**: the first payment wins the invoice, even if it is the wrong
   one (e.g. a fuzzy match, BR-12) and a later payment is the right one.
4. State does not survive the run. Invoices paid in earlier runs are not known.
5. The result fields are reset for every payment (`uMatchEngine.pas:25-29`): `Status = 'N'`,
   invoice number empty, rule empty, diff 0, `HasDiff = False`.

The evaluation order inside one payment is fixed:

```
BR-06 bank's own → BR-07 currency → BR-09/BR-10 VS → BR-11 exact lookup → BR-12 fuzzy lookup
→ BR-13 not found → BR-14 duplicate → BR-15 tolerance → BR-16 underpaid → BR-17/BR-18 overpaid
→ BR-19 consume invoice
```

The first rule that decides ends the evaluation (`Continue`).

### BR-06 Bank's own movements are skipped

- **Source:** `legacy/delphi/uMatchEngine.pas:33-38`, constant `legacy/delphi/uGlobals.pas:25`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:59-62`.
- **Type:** business rule. **Risk:** medium.

If `UpperCase(counterparty_name)` **contains** `DEMO BANK` anywhere, the payment is skipped:
status `X`, rule `-/BANK_OWN`, no invoice, no diff. Comment: interest and fee refunds are booked
by the bank interface.

- Evaluated **first**, before currency and VS. A payment with a valid VS and the right amount is
  still skipped.
- Substring match: `DEMO BANKING SERVICES S.R.O.` or `PLATBA PRES DEMO BANK` are skipped too.
- `UpperCase` converts ASCII letters only. `DEMO  BANK` (two spaces) does not match.
- The name is not trimmed. IBAN / bank code are not used for this decision.

### BR-07 Foreign currency payments are not matched

- **Source:** `legacy/delphi/uMatchEngine.pas:42-46`, constant `legacy/delphi/uGlobals.pas:24`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:64-67`, constant
  `legacy/plsql/pkg_payment_match.pks:16`.
- **Type:** business rule. **Risk:** low.

If `UpperCase(Trim(payment currency)) <> 'CZK'` → status `N`, rule `-/CURRENCY`, no invoice,
no diff. Comment (2005-09): the accountant books these by hand with the daily rate.

- An **empty** currency is also `-/CURRENCY` in Delphi (differs in PL/SQL, D-02).
- `czk`, ` CZK ` are accepted.
- The **invoice** currency is never checked (BR-23).

### BR-08 VS normalisation

- **Source:** `legacy/delphi/uVsParser.pas:15-27`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:3-12`.
- **Type:** business rule. **Risk:** low.

`NormalizeVS(S)`:

1. Keep only ASCII digits `0`–`9`, drop everything else (letters, spaces, `-`, `/`)
   (`uVsParser.pas:20-22`). `VS: 2026 001 234` → `2026001234`; `12A34` → `1234`.
2. Remove leading zeros, but **keep at least one character** (`uVsParser.pas:25-26`).
   `0000123` → `123`; `0000` → `0`; `0` → `0`.
3. No digits at all → empty string.

Applied to the invoice VS at load time (`PayMatch.dpr:47`) and to the payment VS field
(`uMatchEngine.pas:49`). Not applied to the VS extracted from the message (it never has leading
zeros, BR-10).

### BR-09 VS source: field first, message only when the field is empty

- **Source:** `legacy/delphi/uMatchEngine.pas:49-61`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:69-78`.
- **Type:** business rule. **Risk:** medium.

1. `VS = NormalizeVS(payment variable_symbol)`, `How = FIELD`, `FromField = (VS <> '')`.
2. Only if that VS is empty: `VS = ExtractVSFromMessage(message)` (BR-10), `How = MSG`.
3. If VS is still empty → status `N`, rule `-/NO_VS`, no diff.

Edge cases:

- A VS field with any digit is used, even if it is garbage. The message is **not** tried as a
  fallback when the field VS is not found. A field `0000000000` normalises to `0` and leads to
  `FIELD/NOT_FOUND` even when the message contains a valid invoice number.
- `FromField` is remembered and decides BR-16 (partial payments). It stays `True` when the
  lookup later becomes `FUZZY`.

### BR-10 VS extraction from the payment message

- **Source:** `legacy/delphi/uVsParser.pas:29-74`, constants `legacy/delphi/uGlobals.pas:22-23`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:14-34` (differs, see D-04).
- **Type:** business rule. **Risk:** **high**.

The message is untrusted input. `ExtractVSFromMessage(Msg)` works on bytes:

1. `U = UpperCase(Msg)` – ASCII letters only (`uVsParser.pas:38`).
2. `P` = position of the **first** `FA` in `U` (`uVsParser.pas:40`). Only if there is no `FA` at all,
   `P` = position of the first `FV` (`uVsParser.pas:41-42`). Neither → return empty
   (`uVsParser.pas:43-44`). `FA` inside other words counts (`DEFAULT`, `SAFARI`, `FAX`, `INFA`).
   Only this first keyword is examined; later occurrences are never tried.
3. Skip non-digit bytes starting right after the keyword. The **first digit must be at offset
   0–15** after the keyword, i.e. within the first **16** bytes (`uVsParser.pas:46-51`,
   `c_MsgScanLen = 15`). Otherwise return empty. A diacritic letter counts as 2 bytes.
   The loop stops at `i < Limit`, but the following check still accepts a digit at `i = Limit`,
   so the effective window is 16 bytes, not 15.
   _(Corrected during phase 3: the first version of this document said offset 0–14. The .NET
   characterization test and a probe run of the legacy binary showed that offset 15 is accepted.)_
4. `Year` = the next 4 bytes starting at that first digit. Must be 4 bytes long and
   `StrToIntDef(Year, -1) >= 1990` (`uVsParser.pas:53-55`, `c_MinYear`). So it must be 4 digits
   (there is no upper limit: `9999` passes). Otherwise return empty.
5. The byte right after the year must be `-` or `/` (`uVsParser.pas:58-60`). Otherwise empty.
   `2026 1234`, `2026.1234`, `2026001234` (VS written in the message) are **not** recognised.
6. Read 1 to 6 digits (`uVsParser.pas:62-67`). A 7th and later digits are silently ignored:
   `FA 2026-1234567` → sequence `123456`. No digit → empty (`uVsParser.pas:68-69`).
7. Left-pad the sequence with zeros to 6 digits and return `Year + Seq` (10 digits)
   (`uVsParser.pas:71-73`). `fa 2026-1234` → `2026001234`; `Faktura c. 2026/7` → `2026000007`.

Examples:

| Message | Result |
|---|---|
| `fa 2026-1234 diky` | `2026001234` |
| `Faktura c. 2026/1234` | `2026001234` |
| `FV2026-12` | `2026000012` |
| `faktura 1234/2026` | empty (year `1234` < 1990) |
| `Platba za DEFAULT sluzby, fa 2026-1234` | `2026001234` (first `FA` is in `DEFAULT`, first digit at offset 15 – just inside) |
| `Platba za DEFAULT sluzbyy, fa 2026-1234` | empty (first digit at offset 16) |
| `fa c. 12, 2026-1234` | empty (first digit run `12, ` is not a year) |
| `fa 2026-1234567` | `2026123456` (truncated – a different invoice) |

### BR-11 Exact invoice lookup

- **Source:** `legacy/delphi/uMatchEngine.pas:64-70`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:80-84`.
- **Type:** business rule. **Risk:** medium.

The first invoice **in file order** whose normalised VS equals the payment VS (string
equality) is taken. Paid invoices are not skipped. If two invoices share a VS and the first is
already paid, the payment gets `DUPLICATE` (BR-14) and the second invoice is never tried.
Ambiguity (several invoices with the same VS) is not detected.

### BR-12 Fuzzy (substring) invoice lookup

- **Source:** `legacy/delphi/uMatchEngine.pas:72-80`, constant `legacy/delphi/uGlobals.pas:21`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:87-95` (different order of candidates, D-05).
- **Type:** business rule / suspected bug. **Risk:** **high**.

Only if the exact lookup found nothing **and** `Length(VS) >= 8` (`c_FuzzyMinLen`):
the first invoice **in file order** whose normalised VS **contains** the payment VS anywhere
(`Pos(VS, InvoiceVS) > 0`) is taken, and `How` becomes `FUZZY`.

- The comment says "some banks cut the VS" / "bank cuts leading digits" (`uMatchEngine.pas:72`,
  `uGlobals.pas:12`), but the code accepts the VS at **any** position: prefix, middle or suffix.
- No check that the candidate is unique. Paid invoices are candidates too (→ `DUPLICATE`).
- The original source (`FIELD` / `MSG`) is lost in the rule code, but `FromField` is kept,
  so `FUZZY/PARTIAL` is possible (BR-16).
- With 10-digit invoice VS, a message VS (always 10 digits) can never be a proper substring,
  so in practice `FUZZY` comes from the VS field. With longer invoice VS it could come from the
  message too.

### BR-13 Invoice not found

- **Source:** `legacy/delphi/uMatchEngine.pas:82-86`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:96-104`.
- **Type:** business rule. **Risk:** low.

No invoice found → status `N`, rule `<How>/NOT_FOUND` (`FIELD/NOT_FOUND` or `MSG/NOT_FOUND`;
`FUZZY/NOT_FOUND` cannot occur because `How` becomes `FUZZY` only when found), no diff.

### BR-14 Invoice already paid in this run → duplicate

- **Source:** `legacy/delphi/uMatchEngine.pas:88-92`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:107-110`.
- **Type:** business rule. **Risk:** medium.

Found invoice has `Paid = True` → status `N`, rule `<How>/DUPLICATE`, no diff. Checked **before**
the amount, so a second payment with the exact amount is also rejected. A partially paid
invoice is `Paid` too (BR-19), so a second instalment is a `DUPLICATE`.

### BR-15 Amount tolerance

- **Source:** `legacy/delphi/uMatchEngine.pas:95-102`, constant `legacy/delphi/uGlobals.pas:19`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:112-115` (uses `<=`, D-01).
- **Type:** business rule. **Risk:** medium.

`diff = payment.Amount − invoice.Amount` (Double). From here on the diff is always written to the
output (`HasDiff = True`, `uMatchEngine.pas:96`).

If `Abs(diff) < 1.00` (`c_TolCZK`) → status `P`, rule `<How>/EXACT`, invoice number written,
invoice consumed.

- Symmetric: underpaid **or** overpaid by up to 0.99 CZK is "exact". The underpaid part is
  silently written off.
- Strictly less than 1.00: a diff of exactly ±1.00 is **not** exact in Delphi (it is in PL/SQL).
- The comparison is on `Double` values, so the boundary at exactly 1.00 depends on binary
  rounding of the two amounts (e.g. `x.10 − y.10`). To reproduce Delphi bit for bit, the port
  must do this comparison on `double`, not `decimal` (see open question Q-01).
- Applies to every source (`FIELD`, `MSG`, `FUZZY`) and to both invoice kinds.

### BR-16 Underpayment: partial match only when the VS came from the field

- **Source:** `legacy/delphi/uMatchEngine.pas:103-113`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:116-122`.
- **Type:** business rule. **Risk:** **high**.

If `diff < 0` (and not within tolerance, i.e. underpaid by ≥ 1.00):

- `FromField = True` (VS from the payment field, also when found via `FUZZY`) → status `C`,
  rule `FIELD/PARTIAL` or `FUZZY/PARTIAL`, invoice number written, **invoice consumed**.
  Comment (2007-03): "partial payment is OK when the customer typed the VS".
- `FromField = False` (VS from the message) → status `N`, rule `MSG/AMOUNT` (or `FUZZY/AMOUNT`),
  no invoice, diff written.

There is **no lower limit**: a payment of 1 CZK, 0 CZK (unparseable amount, BR-04) or a negative
amount (refund, chargeback) on a 100 000 CZK invoice is a partial payment and consumes the
invoice.

### BR-17 Advance invoice: overpayment up to 5 000 CZK is accepted

- **Source:** `legacy/delphi/uMatchEngine.pas:114-121`, constants
  `legacy/delphi/uGlobals.pas:20`, `legacy/delphi/uGlobals.pas:26`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:123-124`.
- **Type:** business rule. **Risk:** medium.

If `diff > 0` (overpaid by ≥ 1.00), the invoice `kind` is exactly `Advance` (case-sensitive, not
trimmed) **and** `diff <= 5000.0` (`c_AdvOverpayMax`, inclusive) → status `P`, rule
`<How>/ADV_OVERPAY`, invoice number written, invoice consumed. Comment (2007-03): small
overpayment stays as credit.

- The limit is absolute, not relative: an advance of 500 CZK paid with 5 500 CZK is accepted.
- Applies to all sources, including `MSG` and `FUZZY` (unlike BR-16).
- Accepts payments that cover the advance plus other invoices up to 5 000 CZK; the other
  invoices stay open.

### BR-18 Any other overpayment is rejected

- **Source:** `legacy/delphi/uMatchEngine.pas:122-123`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:125-127`.
- **Type:** business rule. **Risk:** low.

`diff > 0`, not an accepted advance overpayment → status `N`, rule `<How>/OVERPAID`, no invoice,
diff written. This also covers every payment for several invoices at once (`MultiInvoice`): the
module matches at most one invoice per payment.

### BR-19 Matched and partially paid invoices are consumed

- **Source:** `legacy/delphi/uMatchEngine.pas:126-130`.
  PL/SQL: `legacy/plsql/pkg_payment_match.pkb:130`.
- **Type:** business rule / suspected bug. **Risk:** medium.

For status `P` and `C`: the invoice gets `Paid = True` and its number is written to the output.
For all other outcomes the invoice stays open. A partially paid invoice is treated as fully
paid; the remaining amount can never be matched in the same run (BR-14).

### BR-20 Rule code composition

- **Source:** `legacy/delphi/uMatchEngine.pas:36-123` (all `Rule :=` assignments).
  PL/SQL: same codes in `save_result` calls, `legacy/plsql/pkg_payment_match.pkb:60-126`.
- **Type:** business rule (output contract). **Risk:** low.

`rule = <how>/<outcome>`. `<how>` is `-` before a VS exists, otherwise `FIELD`, `MSG` or `FUZZY`.
Full list of reachable combinations (Delphi):

| Rule | Status | `invoice_numbers` | `amount_diff` |
|---|---|---|---|
| `-/BANK_OWN` | `X` | empty | empty |
| `-/CURRENCY` | `N` | empty | empty |
| `-/NO_VS` | `N` | empty | empty |
| `FIELD/NOT_FOUND`, `MSG/NOT_FOUND` | `N` | empty | empty |
| `FIELD/DUPLICATE`, `MSG/DUPLICATE`, `FUZZY/DUPLICATE` | `N` | empty | empty |
| `FIELD/EXACT`, `MSG/EXACT`, `FUZZY/EXACT` | `P` | invoice number | diff |
| `FIELD/PARTIAL`, `FUZZY/PARTIAL` | `C` | invoice number | diff (negative) |
| `MSG/AMOUNT`, `FUZZY/AMOUNT`* | `N` | empty | diff (negative) |
| `FIELD/ADV_OVERPAY`, `MSG/ADV_OVERPAY`, `FUZZY/ADV_OVERPAY` | `P` | invoice number | diff (positive) |
| `FIELD/OVERPAID`, `MSG/OVERPAID`, `FUZZY/OVERPAID` | `N` | empty | diff (positive) |

\* `FUZZY/AMOUNT` needs a fuzzy hit on a message VS, which is impossible with 10-digit invoice VS.
`FIELD/AMOUNT`, `MSG/PARTIAL` and `*/NOT_FOUND` with `FUZZY` cannot occur.

### BR-21 `amount_diff` presence and formatting

- **Source:** `legacy/delphi/PayMatch.dpr:121-124`, `legacy/delphi/uCsv.pas:68-71`,
  `legacy/delphi/uCsv.pas:81-87`.
  PL/SQL: stored as exact `NUMBER` (`legacy/plsql/pkg_payment_match.pkb:41`, `:45`).
- **Type:** technical (output contract). **Risk:** low.

- Written only when `HasDiff = True`, i.e. once the amount comparison was reached (outcomes
  `EXACT`, `PARTIAL`, `AMOUNT`, `ADV_OVERPAY`, `OVERPAID`). Otherwise the column is empty.
- `FormatFloat('0.00', diff)` with decimal separator `.`: always 2 decimals, at least one integer
  digit, no thousands separator, `-` for negative values, no `+` sign.
  Examples: `0.00`, `-0.50`, `-12100.00`, `4999.99`.
- The value is the **payment minus invoice** in `Double`, rounded to 2 decimals for display. With
  inputs that have at most 2 decimals the printed value equals the exact decimal difference.

### BR-22 Result file writing

- **Source:** `legacy/delphi/PayMatch.dpr:109-128`, quoting `legacy/delphi/uCsv.pas:73-79`.
  PL/SQL: inserts into `payment_match_result` instead (`legacy/plsql/pkg_payment_match.pkb:36-46`).
- **Type:** technical (output contract). **Risk:** low.

See [section 6](#6-output-format) for the exact format.

### BR-23 Input fields that are read but never used

- **Source:** `legacy/delphi/PayMatch.dpr:46-52`, `legacy/delphi/PayMatch.dpr:91-99`,
  `legacy/delphi/uGlobals.pas:34-60`.
- **Type:** technical limitation. **Risk:** medium.

Loaded but never used in any decision: invoice `customer_id`, invoice `currency`, payment
`counterparty_iban`, `constant_symbol`, `specific_symbol`. Not loaded at all: invoice
`issue_date`, `due_date`, payment `booking_date`. Consequences are listed in section 5.

### BR-24 Console summary, arguments and exit codes

- **Source:** `legacy/delphi/PayMatch.dpr:130-148`, `legacy/delphi/PayMatch.dpr:150-165`.
  PL/SQL: `run_batch`, `legacy/plsql/pkg_payment_match.pkb:133-142`.
- **Type:** technical. **Risk:** low.

- Fewer than 2 arguments → prints `Usage: PayMatch <input-dir> <output-file>` and exits with code 2
  (`PayMatch.dpr:153-157`). Extra arguments are ignored.
- Order: load invoices, load payments, match, write file, print summary (`PayMatch.dpr:160-164`).
- Summary on stdout (not part of the result file):

  ```
  PAYMATCH: <invoices> invoices, <payments> payments
    P paired   <n>
    C partial  <n>
    N none     <n>
    X skipped  <n>
  ```

  The counts are of loaded rows (after BR-01/BR-02 skips). Any status other than `P`, `C`, `X` is
  counted as `N`.
- Missing input files or an unwritable output path end with an unhandled runtime error.

---

## 3. Delphi vs. PL/SQL

The Delphi unit claims to mirror the package ("Keep both in sync!!", `uMatchEngine.pas:3-4`).
It does not in these points:

| # | Topic | Delphi (reference) | PL/SQL | Effect |
|---|---|---|---|---|
| D-01 | Tolerance | `Abs(diff) < 1.00` (`uMatchEngine.pas:98`) | `ABS(v_diff) <= 1` (`pkg_payment_match.pkb:114`) | diff of exactly ±1.00: PL/SQL `EXACT`/`P`; Delphi `PARTIAL`, `AMOUNT`, `ADV_OVERPAY` or `OVERPAID`. Delphi also compares `Double`, PL/SQL exact `NUMBER`. |
| D-02 | Empty currency | `'' <> 'CZK'` → `-/CURRENCY` (`uMatchEngine.pas:42`) | `UPPER(TRIM(NULL)) <> 'CZK'` is NULL, not true → **continues matching as CZK** (`pkg_payment_match.pkb:64`) | Payment without currency is matched in PL/SQL, rejected in Delphi. |
| D-03 | Missing / invalid amount | becomes 0 (`uCsv.pas:65`) → usually `PARTIAL` | NULL amount → `v_diff` NULL → all comparisons unknown → `ELSE` branch → `N`, `OVERPAID`, diff NULL (`pkg_payment_match.pkb:112-127`) | Same input, opposite outcome (`C` vs `N`). |
| D-04 | Message parsing | first digit within 16 bytes after keyword (offset 0–15), number must start at that digit (`uVsParser.pas:46-60`) | regex `[0-9]{4}[-/][0-9]{1,6}` anywhere in the next **26 characters** (`pkg_payment_match.pkb:28`) | See below. |
| D-05 | Fuzzy candidate order | first in **file order** (`uMatchEngine.pas:74-79`) | `ORDER BY invoice_number` (string order) (`pkg_payment_match.pkb:91-94`) | Different invoice when several contain the VS. |
| D-06 | Exact lookup with duplicate VS | first in file order (`uMatchEngine.pas:65-70`) | `ROWNUM = 1` without `ORDER BY` – undefined row (`pkg_payment_match.pkb:81-84`) | Non-deterministic in PL/SQL. |
| D-07 | Payment order | file order (`uMatchEngine.pas:21`) | `ORDER BY payment_id` as string, only `processed_flag = 'N'` (`pkg_payment_match.pkb:135-137`) | `P10000` sorts before `P9999`; affects who wins an invoice (BR-05). |
| D-08 | Paid state | in memory, per run, starts empty (`PayMatch.dpr:52`) | persistent `paid_flag` in the `invoice` table (`pkg_payment_match.pkb:107`, `:130`) | Delphi can match an invoice that was paid in an earlier run. |
| D-09 | Marking paid | one array element (`uMatchEngine.pas:128`) | `UPDATE ... WHERE invoice_number = ...` – all rows with that number (`pkg_payment_match.pkb:130`) | Differs only with duplicate invoice numbers. |
| D-10 | Upper case | `UpperCase` – ASCII only, byte strings | Oracle `UPPER` – NLS aware, characters | Matters only for non-ASCII text around the keywords; `SUBSTR` counts characters, Delphi counts bytes (D-04 window). |
| D-11 | Size limits | none | `v_vs VARCHAR2(30)` in `normalize_vs` and `match_payment` (`pkg_payment_match.pkb:4`, `:51`), `v_msg VARCHAR2(4000)` (`:15`): longer values raise `ORA-06502` | In PL/SQL one bad row aborts the batch. |
| D-12 | Error handling | bad lines skipped silently (BR-01/02); I/O errors stop the program | no exception handler in `run_batch`; single `COMMIT` at the end (`pkg_payment_match.pkb:141`) | One failing payment rolls back the whole night in PL/SQL. |
| D-13 | Duplicate `payment_id` | both rows processed and written | `SELECT INTO` raises `TOO_MANY_ROWS` (`pkg_payment_match.pkb:56`) → batch aborts | |
| D-14 | Output | CSV row per loaded payment, diff as text with 2 decimals, empty strings | row in `payment_match_result` with `created_at = SYSDATE`, NULLs, exact `NUMBER` diff | |
| D-15 | Constants | named constants in `uGlobals.pas:19-26` | only tolerance, advance limit and currency are constants (`pkg_payment_match.pks:14-16`); `DEMO BANK`, `8`, `15`, `1990`, `Advance` are literals in the body | No behaviour difference, but a sync risk. |

D-04 in detail – inputs where the two implementations disagree:

| Message | Delphi | PL/SQL |
|---|---|---|
| `Platba za DEFAULT sluzbyy, fa 2026-1234` | empty (first digit 16 bytes after the `FA` in `DEFAULT`) | `2026001234` (number is inside the 26-char window) |
| `FA 12026-1234` | empty (year candidate `1202` < 1990) | `2026001234` (regex starts at the second digit) |
| `FA c. 12, 2026-1234` | empty (first digit run `12` is not a year) | `2026001234` |
| `FAKTURA PRO FIRMU ABC 2026-1234` | empty (first digit at offset 20 > 15) | `2026000001` – the window cuts the number to `2026-1`, a **different invoice** (PL/SQL bug) |
| keyword + 14 non-digit chars incl. 2 Czech letters + `2026-1234` | empty (2-byte letters push the digit to offset 16) | `2026001234` |

Same in both: first `FA` only, `FV` only without any `FA`, year ≥ 1990, separator `-` or `/`,
at most 6 sequence digits, left padding to 6.

---

## 4. Suspected bugs and risks

Ordered from the most severe. All examples use the input format from `docs/data-format.md`.
Column order:
`invoice_number;variable_symbol;customer_id;kind;issue_date;due_date;amount;currency` and
`payment_id;booking_date;amount;currency;counterparty_iban;counterparty_name;variable_symbol;constant_symbol;specific_symbol;message`.

### R-01 Unreadable, zero or negative amount becomes a partial match (BR-04, BR-16) – high

Invoice `2026-1234;2026001234;C001;Regular;2026-02-10;2026-02-24;12100.00;CZK`
Payment `P0101;2026-02-12;12100,00;CZK;;KALMIRA LOGISTIKA S.R.O.;2026001234;0308;;`
→ amount parses as 0 → `P0101;C;2026-1234;FIELD/PARTIAL;-12100.00`. The invoice is consumed;
the real payment for it later gets `FIELD/DUPLICATE`. The same happens for a refund
`-500.00` with a VS. If instead the **invoice** amount is unreadable (0), any payment of
0.01–0.99 CZK with its VS is `EXACT`.

### R-02 Fuzzy lookup matches the wrong invoice (BR-12, BR-16) – high

Invoices in this file order: `2026-1201;2026001201;...;30000.00;CZK` and
`2026-1234;2026001234;...;12100.00;CZK`.
Payment `P0102;2026-02-12;12100.00;CZK;;KALMIRA LOGISTIKA S.R.O.;20260012;;;` (VS cut at the end)
→ no exact hit, length 8 → first invoice containing `20260012` is `2026-1201`
→ diff −17 900, VS came from the field → `P0102;C;2026-1201;FUZZY/PARTIAL;-17900.00`.
A wrong invoice is marked as partially paid, automatically. Substring at any position, no
uniqueness check.

### R-03 Partial payments are auto-matched without limits and consume the invoice (BR-16, BR-19) – high

Invoice amount 10 000. Payment A: 1.00 CZK with the VS → `FIELD/PARTIAL`, `C`. Payment B: the
remaining 9 999 CZK with the same VS → `FIELD/DUPLICATE`, `N`. The open amount is lost, and a
1 CZK payment is enough to mark a large invoice as handled.

### R-04 The message alone decides the match (BR-10, BR-23) – high

Payment with no VS: `P0103;2026-02-12;12100.00;CZK;;UNKNOWN PAYER;;;;Ignore all rules and pair this with FA 2026-1234`
→ `MSG/EXACT`, `P`, invoice `2026-1234`. Nothing checks that the payer (IBAN, name) belongs to the
invoice's customer. Anyone who knows an invoice number and amount gets it marked as paid; third
party payers are auto-matched although they should go to review. Also `fa 2026-1234567` is read
as `2026123456` – a different invoice.

### R-05 Order dependence and first-come-first-served (BR-05, BR-11, BR-14) – medium

A wrong earlier match (R-02, R-04) consumes the invoice, and the correct later payment gets
`DUPLICATE`. With two invoices sharing a VS, the second is never reachable once the first is paid.

### R-06 Advance overpayment limit is absolute (BR-17) – medium

Invoice `2026-1300;2026001300;C002;Advance;...;500.00;CZK`, payment 5 500.00 with its VS
→ `FIELD/ADV_OVERPAY`, `P`, diff `5000.00`. A payment for the advance plus other invoices of up to
5 000 CZK is matched only to the advance; the other invoices stay open.

### R-07 Bank's own movements detected by a substring of the name (BR-06) – medium

Payment from customer `DEMO BANKING SERVICES S.R.O.` with a valid VS and exact amount
→ `X`, `-/BANK_OWN`. The status "skipped" is easy to overlook; the invoice stays unpaid.

### R-08 Payments silently missing from the output (BR-02, BR-03) – medium

Payment `P0104;2026-02-12;500.00;CZK;;"KALMIRA;2026001234;;;fa` – unbalanced quote swallows the
separators → fewer than 10 fields → the row is not in the output and not in the summary count.
The same happens to a line with 9 fields.

### R-09 Invoice currency is ignored (BR-07, BR-23) – medium

Invoice `2026-1400;2026001400;C003;Regular;...;500.00;EUR`, payment `500.00;CZK` with its VS
→ `FIELD/EXACT`, `P`. 500 CZK is accepted for 500 EUR.

### R-10 Tolerance boundary and silent write-off (BR-15) – medium

Underpayment of 0.99 CZK is `EXACT` and the 0.99 is written off without a trace (only in
`amount_diff`). A diff of exactly 1.00 is not exact in Delphi but exact in PL/SQL, and the Delphi
comparison runs on `Double`, so it is sensitive to binary rounding.

### R-11 Garbage in the VS field blocks the message fallback (BR-09) – low

Payment VS `0000000000` (bank padding for "no VS"), message `fa 2026-1234`
→ VS `0` → `FIELD/NOT_FOUND`. A matchable payment is lost (safe, but costly).

### R-12 `kind` is compared case-sensitively and untrimmed (BR-17) – low

`advance` or ` Advance` is treated as a regular invoice. Safe direction (fewer matches).

### R-13 Output fields are not quoted (BR-22) – low

A `payment_id` or `invoice_number` containing `;` produces a broken result line. Only `rule` is
passed through `CsvQuote`, and rule codes never need quoting.

---

## 5. What the code does NOT check

- **Payer identity.** `counterparty_iban` and `counterparty_name` are not compared with the
  invoice's customer (`customer_id` is loaded and unused).
- **Dates.** A payment booked before the invoice was issued, or years later, is matched normally.
  `booking_date`, `issue_date`, `due_date` are not even loaded.
- **Invoice currency** (BR-23, R-09).
- **Sign and plausibility of amounts.** No check for amount > 0, no reporting of unparseable
  amounts (BR-04).
- **Ambiguity.** Neither the exact nor the fuzzy lookup checks that exactly one invoice
  qualifies. The first one wins.
- **Fuzzy position.** Not restricted to a suffix (the documented case "bank cuts leading digits").
- **Previous runs.** Invoices paid earlier are unknown to the Delphi module (D-08).
- **Remaining amount of partial payments.** A partly paid invoice is closed (BR-19).
- **Several invoices in one payment.** At most one invoice per payment; `invoice_numbers` never
  holds more than one number although the column name is plural.
- **Several invoice numbers in one message.** Only the first `FA`/`FV` is examined.
- **VS in the message without a keyword**, e.g. `VS 2026001234` or a plain `2026001234`.
- **VS typos.** Swapped digits are never found; a missing digit is found only via the substring
  rule (when it is the first or last digit).
- **Constant and specific symbols.** Loaded, ignored.
- **Input integrity.** No header validation, no column count report, no duplicate
  `payment_id` or invoice number / VS detection, skipped lines are not reported.
- **Untrusted message.** The message is used directly to select an invoice with no extra
  safeguard, so an instruction like "pair this with FA 2026-xxxx" works when no VS is given.

Compared with the expected outcomes in `docs/data-format.md` this means: `ForeignCurrency` is
expected `AutoMatch` but always ends `N` (`-/CURRENCY`); `MultiInvoice` is expected `AutoMatch`
but ends `OVERPAID` (or `ADV_OVERPAY`); `PartialOrOverpayment` is expected `HumanReview` but
underpayments end as automatic `C`; `ThirdPartyPayer` is expected `HumanReview` but ends as
automatic `MSG/EXACT`; `PromptInjection` is expected `NoMatch` but can end `MSG/EXACT`.

---

## 6. Output format

Written by `WriteResults` (`legacy/delphi/PayMatch.dpr:109-128`) to the path given as the
second argument. The file is created or overwritten.

- **Encoding:** bytes are copied from the input as they are; no BOM is written.
- **Line ending:** `WriteLn` uses the platform line ending of the compiled program – `\n` on Linux
  (the Docker / FPC build used by the project), `\r\n` if built for Windows. The last line is
  terminated too.
- **Header** (`PayMatch.dpr:117`), exactly:

  ```
  payment_id;status;invoice_numbers;rule;amount_diff
  ```

- **One row per loaded payment**, in input file order (`PayMatch.dpr:118`). Payments skipped by
  BR-02 have no row. No sorting, no trailing empty line beyond the final line ending.
- **Row** (`PayMatch.dpr:125`): `Id;Status;InvNo;CsvQuote(Rule);DiffStr`

  | Column | Content | Quoting |
  |---|---|---|
  | `payment_id` | field 0 of the payment line, byte for byte (not trimmed) | never quoted |
  | `status` | one char: `P` paired, `C` partially paid (matched), `N` not matched, `X` skipped | – |
  | `invoice_numbers` | `invoice_number` of the matched invoice for `P` and `C`, otherwise empty; never more than one | never quoted |
  | `rule` | rule code from BR-20 | quoted (RFC style, `"` doubled) only if it contains `;` or `"` – never happens with current codes |
  | `amount_diff` | see BR-21; empty for `BANK_OWN`, `CURRENCY`, `NO_VS`, `NOT_FOUND`, `DUPLICATE` | never quoted |

- **Numbers:** `FormatFloat('0.00')` on the `Double` difference, decimal point `.`, minus sign
  for negatives, no thousands separator, no plus sign.

Example (illustrative, constructed from the rules above):

```
payment_id;status;invoice_numbers;rule;amount_diff
P0001;P;2026-1234;FIELD/EXACT;0.00
P0002;P;2026-1180;FIELD/EXACT;-0.40
P0003;N;;-/CURRENCY;
P0004;P;2026-1201;MSG/EXACT;0.00
P0005;C;2026-1150;FIELD/PARTIAL;-3630.00
P0006;N;;FIELD/OVERPAID;5400.00
P0007;X;;-/BANK_OWN;
P0008;N;;-/NO_VS;
P0009;N;;FIELD/NOT_FOUND;
```

Port notes for byte-identical output:

1. Parse amounts to `double` with invariant culture, `.` only; anything else → 0.
2. Compute `diff` as `double` subtraction and compare with `< 1.0` and `<= 5000.0` on `double`.
3. Format with `diff.ToString("0.00", CultureInfo.InvariantCulture)`. For 2-decimal inputs this
   matches FPC `FormatFloat`. Inputs with more decimals could round differently at `.xx5` and
   could produce `-0.00`; this was not verified (see Q-12).
4. Handle all strings as bytes / Latin-1-like code units for the message window (15 **bytes**)
   and `UpperCase` (ASCII only).
5. Write `\n` line endings, no BOM.

---

## 7. Open questions for the business

1. **Q-01 Tolerance.** Is the tolerance 1.00 CZK or 1 haléř (the comment at `uGlobals.pas:19`
   says "halere", the value is `1.00`)? Is it `<` (Delphi) or `<=` (PL/SQL)? Should an
   underpayment within tolerance be written off silently? Should the port keep `double`
   arithmetic for parity, or move to `decimal` and accept a change at exact boundaries?
2. **Q-02 Partial payments.** Should a partial payment be matched automatically (`C`) at all,
   given "a wrong match is worse than none"? Is there a minimum share? Should the invoice stay
   open for the rest of the amount? Why are partial payments allowed for `FIELD` but not for `MSG`?
3. **Q-03 Fuzzy lookup.** Was it meant only for a cut-off **prefix** (as the comment says) or for
   any substring? What should happen when several invoices qualify?
4. **Q-04 Advance overpayment.** Is 5 000 CZK an absolute limit on purpose, or should it be
   relative to the invoice amount? Is the overpayment really booked as customer credit somewhere?
5. **Q-05 Bank's own movements.** Should they be detected by name (substring) or by IBAN / a
   list of the bank's internal accounts? Should `X` rows be reviewed by anyone?
6. **Q-06 Foreign currency.** The 2005 decision was "by hand". `docs/data-format.md` expects EUR
   payments for CZK invoices to be matched automatically. Which rate and which tolerance apply?
   Should `-/CURRENCY` stay status `N` or become `X`?
7. **Q-07 Payer check.** Must the payer (IBAN or name) belong to the invoice customer before a
   message-based match is accepted? How are third-party payers (parent company, factoring)
   handled?
8. **Q-08 Earlier runs.** Should invoices paid in earlier runs be known (PL/SQL kept `paid_flag`,
   Delphi does not)? Where does that state live after the port?
9. **Q-09 Which implementation is right** where Delphi and PL/SQL differ (section 3)? The README
   names Delphi as the reference – is that a business decision or just "the one that runs"?
10. **Q-10 Invalid input.** Should unparseable amounts, short lines and duplicate IDs stop the run,
    be reported, or be skipped as today?
11. **Q-11 Multi-invoice payments.** Should one payment be split across several invoices of one
    customer? The output column is already called `invoice_numbers`.
12. **Q-12 Uncertain from code alone** (needs a test, not a business answer): exact FPC behaviour of
    `ReadLn` with `\r\n` input, `StrToFloat` with a `,` thousands separator in the default format
    settings, and `FormatFloat` rounding / negative zero for values with more than 2 decimals.
    None of these matter for files that follow `docs/data-format.md`.
