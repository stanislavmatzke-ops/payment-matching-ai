// The state of the hosted app. Committed on purpose: every run of infra/deploy-infra.sh reads it, so a
// later run cannot silently drop the job, a sign-in provider or the custom domain. No secrets here:
// client ids are public, secrets are Key Vault references (ADR 0016).
using './main.bicep'

param githubRepository = 'stanislavmatzke-ops/payment-matching-ai'
param demoTenantId = 'afb0d1ca-fc98-4d44-bab5-0d68475985f0'
param aiMonthlyUsd = '0.50'

// Step by step (README "Deploy to Azure"): shared pieces first, then the apps once db-connection exists,
// then the job once anthropic-api-key exists.
param deployApps = false
param jobEnabled = false

param microsoftClientId = ''
param googleClientId = ''
param adminConfigured = false

param customDomain = ''
param customDomainCertificate = ''

// Set by infra/deploy-infra.sh: operatorPrincipalId, webImage, jobImage.
param operatorPrincipalId = ''
