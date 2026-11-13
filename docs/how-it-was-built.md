# How it was built

> 🇬🇧 English · 🇨🇿 [Česky](cs/how-it-was-built.md)

This project – code, tests, infrastructure and documentation – was built by one developer with an AI
coding agent (Claude Code), in twelve phases over a few days. This page is the short version of the
progress notes in [PLAN.md](PLAN.md): how the work was set up, what the developer checked, what the
agent got wrong and what caught it.

## The setup: spec first, plan before code

Three files steer the agent, and they are in the repository on purpose:

- **[SPEC.md](SPEC.md)** – what to build and why, written by the developer before any code (the
  original Czech brief is kept in [spec-original-cs](spec-original-cs/SPEC.md)). It changed during
  the project, always as a visible amendment with a date and an ADR.
- **[PLAN.md](PLAN.md)** – the phases, their status, and running notes: decisions, deviations from
  the SPEC, money spent on model calls, and the agent's mistakes.
- **[AGENTS.md](../AGENTS.md)** – rules for any agent: the workflow, hard rules (synthetic data only,
  no secrets, CI without an API key, a wrong match is worse than none, statement text is untrusted),
  conventions and the commands.

Every phase ran the same way, mostly in a **fresh agent session** that knew only these files and the
code:

1. The agent read PLAN, SPEC and the ADRs and proposed a short plan: files, steps, tests, new
   dependencies with a reason.
2. **The developer approved it** (or changed it). Most plans were approved unchanged; the valuable
   moments were the questions the agent asked before approval, when the SPEC had not settled
   something.
3. The agent implemented it in small commits with tests, ran build, tests, format and the eval, and
   **stopped** to show what was built.
4. Decisions went into ADRs (18 in the end), mistakes into the PLAN notes.

The developer's job was the "what" and the "whether": the specification, approving plans, product
judgement, anything that costs money (every paid model call and every Azure change needed an explicit
yes), and using the result as a real user. The agent did the "how" and checked its own work with
tests.

## What the agent got wrong – and what caught it

Most mistakes were caught by tests the agent had written itself, often the same day. The interesting
ones were not typos:

- **The AI-extracted business rules were thorough, but one was off by one.** A separate agent session
  without any context read the legacy code and found all 13 planted rules plus seven real issues
  nobody had planted. A characterization test then showed that one rule's window was 0–15, not 0–14.
  *Caught by:* the golden master and unit tests from the document's own examples.
- **Validation had a hole that the model never found, but a stub did.** A deterministic stub matched
  a euro payment to two unrelated invoices whose sum happened to fit the exchange-rate band. A test
  with a random model had passed by chance. *Caught by:* an exhaustive test that tries every
  combination of candidates as "the model's answer" – 45 holes found, all closed.
- **Validation checked that a proposal fits, not that it is the only one.** On the first eval run 30
  ties passed and 8 went to the wrong invoice. *Caught by:* the eval gate in CI (precision 84 %, red
  build) → a new rule `AMBIGUOUS`. The exhaustive test from the previous phase had been good, but over
  data without ties: a test is only as good as its data.
- **The prompt depended on the operating system.** Recordings made on Windows did not replay in Linux
  CI: JSON in the prompt used `\r\n`. *Caught by:* CI on another OS → a fixed `\n` and a test.
- **A library logged what it promised not to.** The MCP SDK puts a failed tool's answer – which echoes
  the agent's arguments – into the span status. *Caught by:* a telemetry test with marked arguments →
  a redaction processor before every exporter.
- **Tests that pass alone and fail together.** Telemetry listeners are process-wide, so tests running
  in parallel saw each other's spans: once as wrong counts, once (the last flaky test, fixed in phase
  12) as a list modified from another thread while being copied. *Caught by:* repeated full runs; the
  fix is to read only the test's own trace.
- **A test that depended on a random seed** passed locally and failed in CI. Lesson written into the
  notes: a test over generated data uses a fixed seed or inserts its own rows.
- **The Windows shell, again and again.** A heredoc with Python started an interactive interpreter and
  hung; `sed` with backslashes in paths silently replaced nothing (five times). Caught by a check
  listing or the build every time, and the agent moved to its edit tools – but kept forgetting. Small,
  repeated, and worth knowing about when working with agents on Windows.

None of these reached production. The pattern: the agent rarely wrote code that did not compile, but
it did write code that was plausible and wrong in ways only data, a second platform or parallelism
reveal.

## What only the deployment revealed

Four bugs appeared on the first deployment to Azure, and no test could have caught them: Azure gives
the `public` schema of a new database to another owner (first migration: permission denied); Container
Apps keeps a Key Vault secret's value until a new revision, so a rotated password silently broke the
running app; the Application Insights exporter adds rate-limited sampling that dropped the only trace
of a short job; and GitHub's OIDC subject now contains immutable ids, which the federated credential
did not match. Each was diagnosed by the agent from logs and SDK diagnostics, and fixed in the scripts
or as a parameter.

One more limit showed up here: Claude Code's safety check did not let the agent apply infrastructure
changes, even after the developer approved them. The developer ran the Bicep deployment; the agent
wrote it and verified the result. That turned out to be a good split.

## What only a user found

After launch the developer used the app as a user, and within an hour found things no test would:
a new person saw AI payments as "waiting for AI" until the next morning (a direct consequence of the
rule that visitors never start a model), the queue did not show the invoice next to the payment, the
numbers "6 AI calls" and "7 AI decisions" looked contradictory (six paid calls plus one cached answer),
rule codes like `FIELD/EXACT` meant nothing to a person, and nothing said whether an AI match could be
reversed. The fixes kept every rule: a weekly starter set precomputed by the job so that sign-in reads
AI decisions from the cache ([ADR 0017](adr/0017-instant-ai-for-new-sandboxes-from-a-weekly-starter-set.md)),
and a UI that says what the AI added, lets a person undo it and shows how often people do
([ADR 0018](adr/0018-ui-after-the-first-user-test.md)).

## In numbers

- 12 phases, 18 ADRs, ~400 tests (database tests against a real PostgreSQL), CI without an API key.
- Model calls during development: under $1.50 in total (recordings for the evals, variance runs and
  the first runs in Azure), each run approved with an estimate first.
- Hosting: a few dollars a month; AI in production within a $0.50 monthly budget.

## Takeaways

- **Write the specification and the rules down, in the repository.** A fresh session with only SPEC,
  PLAN, AGENTS.md and the code produced plans that needed almost no correction.
- **Approve plans, not diffs.** Reviewing a ten-line plan is where a person adds the most; the tests
  check the diff.
- **Make the agent prove things with tests it cannot argue with:** a golden master, exhaustive
  validation tests, an eval gate, leak tests with identical ids in two tenants.
- **Keep money and infrastructure behind an explicit yes.** It cost a few interruptions and prevented
  any surprise.
- **Plan for deployment and for the first hour with a real user as separate phases.** Both found
  problems that no amount of testing would.
