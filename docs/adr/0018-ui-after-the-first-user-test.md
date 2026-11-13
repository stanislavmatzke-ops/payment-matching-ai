# ADR 0018: The UI after the first user test – AI made visible, undo, a usable list, Czech

- Status: Accepted
- Date: 2026-11-13
- Amends: ADR 0012 (a person's decisions), ADR 0017 (starter set size)

## Context

The developer used the hosted app as its first user and found what no test had: the app looked empty
(20 starter payments, 2 in the queue), "AI cost (6 calls)" did not match the 7 payments the AI decided
(one answer came from the cache), it was not clear *which* payments the AI handled or that it could be
corrected, rule codes such as `FIELD/EXACT` meant nothing to a non-developer, the list had no search,
sort or pages, and the app was English only. Constraints stay: no JavaScript (CSP without scripts),
writes only through `ReviewActions`, a visitor never starts a model, tenant isolation in the data layer.

## Decision

**AI made visible.** A "What AI added" card (dashboard and demo): payments the AI decided, matched on its
own, sent to a person with a proposal, injections stopped, paid calls versus answers reused from the
cache, and *AI precision as seen by people* – the share of the AI's own matches nobody undid. Every
payment counted is one the rules could not settle (the AI layer only sees those). The payment detail
shows a "Decided with AI" banner: why the rules could not settle it, what the model did, that code
checked it. The list filters by decider (`?by=ai|rule|person|policy|none`).

**Undo an AI match** (amends ADR 0012). A match the AI made on its own can be undone once by a person:
`GET/POST /payments/{id}/undo` with the same form as a manual match (invoice ids and check codes only,
antiforgery, rate limit). The person picks other invoices (same checks; the AI's invoices count as open)
or none. It is the person's decision (`undone_ai` in `reviews`); the AI's decision stays in the audit
trail unchanged; the freed invoice is open to people, while the pipeline stays conservative and does not
auto-match it again. Rule matches are not undone here. The test listing the write endpoints names the
new one.

**Rule codes in words.** Each code has a sentence (`Format.RuleLegend`, every code the rules can return,
checked against `PaymentMatcher`); the detail shows the sentence with the code beside it, the chip has it
as a tooltip, and a public page "How decisions are made" (`/how`) explains the steps, who decides and all
codes.

**A usable list.** `/payments` searches payer, VS, message, amount and id, filters by state and decider,
sorts by date, amount, payer or id (column headers) and pages by 25 – on the server over the loaded
sandbox (hundreds of payments at most), with links and a GET form, no script. Unknown query values are
ignored; the search text is compared in memory and written back encoded.

**Czech next to English.** English stays the default. `Text.T("English text")` returns Czech when the
request culture is Czech; the English text is the key, so pages still read naturally in code. The
language is a functional cookie set by `GET /lang/{en|cs}`, which returns only to a local path; the
switch is in the header next to the person's name (and on public pages). Amounts use Czech number
format in Czech. Razor's encoder allows all Unicode ranges, so Czech letters are text, not entities
(markup characters are still encoded). A test renders every page in Czech and fails on any missing
translation.

**More data at the start:** the weekly starter set has 40 payments instead of 20 (a queue of ~5–8). With
ADR 0017 it costs ~10–16 model calls a week, once, whatever the number of sign-ins.

Smaller items from the same test: a favicon (CSP gains `img-src 'self'`), the landing page's flow icons
centred (a CSS selector made the icon frame `display: block`), a footer link "About this project"
(`Links:About`, a Bicep parameter, https only).

## Consequences

- People can see and correct what the AI did, and the dashboard turns their corrections into a precision
  number – feedback from use next to the evals' gold set.
- One more write endpoint, deliberately listed in the test of write endpoints.
- Every new text needs a Czech translation, or the language test fails.
- The pipeline does not re-match an invoice freed by an undo; a person matches it if needed.
