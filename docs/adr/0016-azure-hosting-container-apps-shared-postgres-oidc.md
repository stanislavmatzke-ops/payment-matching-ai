# ADR 0016: Hosting in Azure – Container Apps, a shared PostgreSQL server, Key Vault and OIDC deployment

- Status: Accepted
- Date: 2026-11-13

## Context

SPEC 9b asks for a public app in Azure for a few hundred CZK a month: Container Apps (web scaling to
zero, a Container Apps Job for the daily run), PostgreSQL Flexible Server on the lowest tier, Key Vault,
Application Insights, infrastructure as code and deployment from GitHub Actions without stored
passwords. The developer already runs another project in the same subscription with a Burstable B1ms
PostgreSQL server (Poland Central) and a Container Apps environment (Germany West Central). CI must keep
running without Azure, an API key or a telemetry backend.

## Decision

**One resource group `rg-payments`, one Bicep file (`infra/main.bicep`)**: Container Apps environment
(Consumption, no VNet), web app `ca-payments-web` (0.25 vCPU, 0.5 GiB, 0–1 replica, HTTPS only), job
`caj-payments-daily` (cron `0 5 * * *` UTC, parallelism 1, one completion, no retry, 30 min timeout,
`--mode live --migrate`), Key Vault (RBAC), Log Analytics (daily cap 0.1 GB, 30 days) with
workspace-based Application Insights, and two user-assigned identities. The settings that describe the
running app (apps on, job on, sign-in client ids, custom domain) are committed in
`infra/main.bicepparam`, so a later run cannot silently drop one of them. azd was not used: it adds a
tool, and the job and the shared database do not fit its templates cleanly.

**The database is a database on the existing server, not a new server.** A B1ms server costs about
$16–18 a month, more than everything else together. `infra/bootstrap-db.sh` (run once) creates the
database `payments` and a login role `payments_app` that owns only that database: no admin rights,
`CONNECT` revoked from `PUBLIC` on it. Azure creates the schema `public` of a new database owned by
`azure_pg_admin`, so the script gives it to the role (found on the first deployment). The admin
password is read from the other project's Key Vault only inside the script, through a temporary
firewall rule for the operator's address that is removed on exit. Entra ID authentication for
PostgreSQL would remove the password entirely, but it means changing the other project's server; the
password lives only in Key Vault.

**Secrets are Key Vault references only**: `db-connection`, `anthropic-api-key`,
`microsoft-client-secret`, `google-client-secret`, `admin-accounts`. Container Apps reads them with the
runtime identity (Key Vault Secrets User); the template never sees a value. Secrets a configuration
does not need are not referenced, so the web app can run before the sign-in providers are registered.
The person who deploys gets Key Vault Secrets Officer and puts secrets in from their own terminal.
The Application Insights connection string is a plain setting (it allows sending telemetry, not
reading data). Container Apps keeps a secret's value until a new revision or a secret update, so a
rotation needs `az containerapp update --revision-suffix …` (web) or `job secret set` (job); the
bootstrap therefore never rotates the database password unless asked (`RESET_PASSWORD=1`).

**Images are built by the .NET SDK** (`dotnet publish -t:PublishContainer`, no Dockerfile) on chiseled
Ubuntu (`noble-chiseled-extra`: no shell, non-root, ICU and time zones kept) and pushed to
**ghcr.io as public packages** with the workflow's own token. Pulling a public image needs no
registry credential in Azure; Azure Container Registry Basic would cost about $5 a month for the same.
The images contain no secrets and only synthetic data; the source follows in phase 12.

**Two kinds of change, two kinds of rights.** `infra/deploy-infra.sh` deploys the Bicep file locally
by a person with Owner on the group (it creates role assignments) and keeps the images that run.
`.github/workflows/deploy.yml` runs after a green CI on `main`: it pushes images and, signed in through
OIDC as `id-payments-deploy` (federated credential for the GitHub environment `production`,
Contributor on `rg-payments` only), points the app and the job at them and runs smoke tests. The
pipeline cannot change role assignments or read Key Vault secrets, and there is no password in GitHub:
the repository variables `AZURE_CLIENT_ID/TENANT_ID/SUBSCRIPTION_ID` are identifiers. Without them
(forks, CI) the Azure steps are skipped. CI itself compiles the Bicep file and builds both images into
archives.

**What the app needed for Container Apps:**

- The Data Protection key ring is stored in PostgreSQL (`data_protection_keys` in
  `OperationsDbContext`, a 30-line `IXmlRepository`, no package). With scale-to-zero, keys kept in the
  container would sign everyone out and invalidate open forms after every idle period. The keys are
  not encrypted at rest; whoever can read that table can read all data anyway.
- TLS ends at the ingress: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` makes the OIDC callback `https`
  and gives the rate limiter the client's address instead of the proxy's. A test covers both settings.
- Migrations run in the web app at start-up (`Database:MigrateOnStartup`) and in the job (`--migrate`).

**Two details found on the first deployment:**

- GitHub's OIDC token now carries immutable ids in its subject
  (`repo:<owner>@<owner id>/<repo>@<repo id>:environment:production`); a federated credential written
  as `repo:<owner>/<repo>:…` matches nothing. The exact subject is a parameter (`githubOidcSubject`);
  the `azure/login` log prints it.
- The Azure Monitor trace exporter adds a rate-limited sampler by default, which dropped the single
  trace of a one-shot run (daily job, CLI) before its budget filled; metrics still arrived. The exporter
  is set to a fixed ratio of 1.0, as ADR 0015 intended (always-on sampling).

**Custom domain:** a CNAME and an `asuid` TXT record, then `infra/bind-domain.sh` adds the host name
with a free managed certificate; its name goes into `main.bicepparam`.

## Consequences

- Hosting costs about $0–2 a month: the environment, Key Vault and identities are free or nearly so,
  logs stay inside the free allowance, the web app is billed only while awake. See `docs/cost.md`.
- The first request after an idle period waits for a cold start (about 10–20 s).
- The app shares a database server with another project: a server outage or maintenance affects both,
  and the server's `AllowAzureServices` rule admits connections from any Azure address (with TLS and a
  password). A VNet with private access, PostgreSQL row-level security (ADR 0011) and Entra
  authentication remain candidates if the app grows.
- Every push to `main` deploys after CI; a failed smoke test fails the workflow but does not roll back.
