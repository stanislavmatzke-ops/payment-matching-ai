// The state of the hosted app. Committed on purpose: every run of infra/deploy-infra.sh reads it, so a
// later run cannot silently drop the job, a sign-in provider or the custom domain. No secrets here:
// client ids are public, secrets are Key Vault references (ADR 0016).
using './main.bicep'

// GitHub's OIDC subject carries the immutable owner and repository ids, not only the names.
param githubOidcSubject = 'repo:stanislavmatzke-ops@231474937/payment-matching-ai@1455081591:environment:production'
param demoTenantId = 'afb0d1ca-fc98-4d44-bab5-0d68475985f0'
param aiMonthlyUsd = '0.50'
// $0.05 per call allowed only ~8 calls a run with $0.50 a month, too few for the weekly starter set
// (40 payments); a call costs ~$0.0004 (small model) to ~$0.01 (escalation). The month stays capped.
param aiReservePerCallUsd = '0.02'

// Step by step (README "Running in Azure"): shared pieces first, then the apps once db-connection exists,
// then the job once anthropic-api-key exists.
param deployApps = true
param jobEnabled = true

param microsoftClientId = 'f40e1bb1-6256-4bea-8ff0-77c850d31ca6'
param googleClientId = '904430889483-etg5kl64n17cl77r3kdquund2tkjtum4.apps.googleusercontent.com'
param adminConfigured = true

// Ready to be shared: more sign-ups a day (no AI cost, ADR 0017) and no cold start.
param maxNewSandboxesPerDay = 30
param webMinReplicas = 1

// Shown on /privacy as the data controller and the address for data requests.
param operatorName = 'Stanislav Matzke'
param operatorEmail = 'kontakt@stanislavmatzke.com'

// Footer link "About this project"; later the case study.
param aboutUrl = 'https://stanislavmatzke.com'

param customDomain = 'payments.stanislavmatzke.com'
param customDomainCertificate = 'mc-cae-payments-payments-stanisl-7480'

// Set by infra/deploy-infra.sh: operatorPrincipalId, webImage, jobImage.
param operatorPrincipalId = ''
