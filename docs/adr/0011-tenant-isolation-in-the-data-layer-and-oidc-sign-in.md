# ADR 0011: Tenant isolation in the data layer and sign-in with Microsoft or Google

- Status: Accepted
- Date: 2026-11-12

## Context

The hosted app (SPEC 9b) lets anyone sign in with a Microsoft or Google account and work in an own
sandbox: a fictional company with invoices, payments and a queue. Nobody may see another person's
data. Until now decisions lived in a JSON Lines file (ADR 0006) and the MCP server served one
"workspace" per process (ADR 0010). Constraints: no secrets in the repository, CI without an API key
and without a real identity provider, and a visitor must never trigger a model call.

## Decision

**EF Core on PostgreSQL (`src/PaymentMatching.Data`).** Tables `tenants`, `users`, `customers`,
`invoices`, `payments`, `decisions`, `invoice_matches`, `proposals`. Every row except the tenant
itself carries `TenantId`, which is part of the primary key, so two sandboxes can use the same ids
(`P0001`, `2026-1001`) and only the tenant tells them apart. Deleting a tenant cascades to all rows.

**Isolation is enforced by `PaymentsDbContext`, not by callers:**

- a global query filter `TenantId == current tenant` on every entity (the tenant row too),
- `SaveChanges` stamps new rows with the current tenant and refuses to add, change or delete a row
  of another tenant (`TenantIsolationException`),
- no tenant set → every query and every save throws (fail closed),
- the tenant (`ITenantContext`) is set once per request from the signed-in user's cookie, or once per
  MCP process from its `--tenant` option; it cannot be switched,
- only `TenantAdmin` may look across tenants: find the tenant of a sign-in, count new sandboxes,
  delete inactive ones. A test fails if any other file in `src/` uses `IgnoreQueryFilters` or raw SQL.

**Idempotency moves into the schema (ADR 0006).** Primary keys `(tenant, idempotency key)` on
`decisions` and `(tenant, invoice)` on `invoice_matches`: a statement line is decided once and an
invoice is matched automatically once, even when two runs race. The pipeline uses the same rules in
memory through `IDecisionStore`; `DecisionLog` (JSONL) stays for the CLIs, evals and CI.

**Sign-in: one standard OpenID Connect handler, two providers.** Authorization code flow with PKCE,
scopes `openid profile email`, tokens not saved, claims not mapped. Microsoft uses the `common`
endpoint (work, school and personal accounts); because the issuer then depends on the user's
directory, a small validator requires the issuer to be the one of the token's own `tid`. Google uses
`accounts.google.com`. The identity is `(provider, sub)`; we keep only name and e-mail besides it. A
provider is registered only when its client id and secret are configured (user-secrets locally, Key
Vault in Azure later). The session is a `__Host-` cookie: HttpOnly, Secure, SameSite=Lax, 8 hours.
Microsoft.Identity.Web was not used: it is Entra-specific and much larger than this need.

**First sign-in creates the sandbox** (`SandboxProvisioner`): tenant, user, synthetic starter data
from the generator (20 payments, ~25–30 invoices, the usual case mix) and a run of the rules layer
only. Payments that need AI stay undecided until the scheduled job (phase 9), so a sign-in never
calls a model. New sandboxes are limited per day (`Sandboxes:MaxNewPerDay`); sandboxes unused for
30 days are deleted (`TenantAdmin.DeleteInactiveAsync`, scheduled in phase 9).

**Local development without an identity provider:** a dev login (`/dev-login?name=alice`) that
exists only with `Auth:DevLogin=true`, and the app refuses to start with that flag outside the
Development environment.

**The MCP server moves onto the data layer:** `payments-mcp --tenant <id>` reads that tenant through
the filtered context and saves proposals under it. Over stdio the caller is whoever starts the
process; no tool takes a tenant. A hosted MCP endpoint would take the tenant from an OAuth token
the same way; that is not built.

**Tests run against a real PostgreSQL** (Testcontainers, one container per test assembly, a fresh
database per test). In-memory providers do not enforce unique keys or behave like PostgreSQL.

## Consequences

- Leak tests with identical ids in two tenants cover every table (the list comes from the EF model,
  so a new table is covered automatically and must be tenant-owned), `Find` across tenants, updates,
  deletes, inserts with a foreign tenant, bulk updates, missing tenant, the HTTP API and the MCP tools.
- The OIDC wiring is tested without network or secrets: the provider metadata is supplied in the
  test, and the test checks the redirect (authority, client id, scopes, PKCE, callback).
- `dotnet test` now needs Docker locally (GitHub runners have it).
- PostgreSQL row-level security would be a second, independent layer. It is not used yet; it is a
  candidate for the Azure phase together with a database role per app.
- New dependencies: `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`
  (design time only), `Microsoft.AspNetCore.Authentication.OpenIdConnect`; tests:
  `Testcontainers.PostgreSql`, `Microsoft.AspNetCore.Mvc.Testing`.
