# ADR 0004: Port legacy rules faithfully first, then change them deliberately

- Status: Accepted
- Date: 2026-11-12

## Context

The legacy matching rules contain bugs that cause wrong matches (business-rules.md, R-01, R-02).
Fixing them while porting is tempting, but then nobody can tell a porting mistake from an
intended fix. The golden master (ADR 0003) gives us the exact legacy output for 1 300 payments.

## Decision

1. **Faithful port first.** The .NET rules reproduce the legacy output byte for byte, bugs
   included. This state is tagged `phase3-faithful-port`.
   - Files are read as Latin-1 (one byte = one char) so byte-based legacy behaviour is reproduced
     without special cases.
2. **Refactor under the safety net.** Changes that should not change behaviour (e.g. `double` →
   `decimal`) must keep the golden master byte-identical.
3. **Deliberate changes with an ID (F1, F2, …).** Each one is listed in `docs/modernization.md`
   with its reason. From then on the golden test checks that **every line that differs from legacy
   is explained by one of the documented changes**. The port output is committed as
   `legacy/golden/*/port_matches.csv` so differences are visible in pull requests.
4. **No permanent "legacy mode" switches.** The faithful version lives in git history, not behind
   configuration flags.
5. **Only clear bugs are fixed in the port.** Business questions (partial payments, payer check,
   tolerance, foreign currency) stay as in legacy until the business decides. Routing uncertain
   cases to a human belongs to the next layer (phase 4).

## Consequences

- Porting errors and intended changes are never mixed. The faithful port passed byte for byte on
  the first run; later, the "explained differences" test found two knock-on effects of F1 we had
  not predicted.
- Each new change needs a classifier rule in the golden test. That is extra work, and it is the
  point: a behaviour change has to be explained before it is accepted.
- The golden master also found a mistake in the AI-written business-rules.md (message window off
  by one). Running code beats reading code for line-level precision.
