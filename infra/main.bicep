// Payment matching – hosting in Azure (phase 11, ADR 0016).
//
// Everything lives in one resource group: Container Apps environment, the web app (scale to zero), the daily
// Container Apps Job, Key Vault, Log Analytics + Application Insights and two user-assigned identities
// (runtime and GitHub deploy). The database is a database + role on an existing PostgreSQL Flexible Server,
// created once by infra/bootstrap-db.sh; its connection string is the Key Vault secret `db-connection`.
//
// Run locally (role assignments need Owner on the group): infra/deploy-infra.sh. GitHub Actions only swaps
// images (Contributor on this group, no access to secrets or roles).
targetScope = 'resourceGroup'

@description('Region of the resource group.')
param location string = resourceGroup().location

@description('Prefix of resource names.')
param name string = 'payments'

@description('Subject of the GitHub OIDC token allowed to deploy. GitHub puts immutable ids into it: repo:<owner>@<owner id>/<repo>@<repo id>:environment:production (the azure/login log prints it).')
param githubOidcSubject string

@description('Object id of the person who runs this deployment; gets Key Vault Secrets Officer to put secrets in.')
param operatorPrincipalId string

@description('false = only the shared pieces (identities, Key Vault, monitoring, environment). The apps need the secrets first.')
param deployApps bool = false

@description('Web image, e.g. ghcr.io/owner/payments-web:sha-<commit>.')
param webImage string = ''

@description('Job image, e.g. ghcr.io/owner/payments-job:sha-<commit>.')
param jobImage string = ''

@description('Create the daily job (needs the Key Vault secret anthropic-api-key; it calls the model within the AI budgets).')
param jobEnabled bool = false

@description('Cron (UTC) of the daily job.')
param jobSchedule string = '0 5 * * *'

@description('The read-only demo sandbox (any GUID).')
param demoTenantId string

@description('Monthly AI budget in USD (Ai:Budget:MonthlyUsd). The Anthropic console spend limit is the hard backstop.')
param aiMonthlyUsd string = '0.50'

@description('Microsoft sign-in client id; empty = off. Needs the Key Vault secret microsoft-client-secret.')
param microsoftClientId string = ''

@description('Google sign-in client id; empty = off. Needs the Key Vault secret google-client-secret.')
param googleClientId string = ''

@description('true when the Key Vault secret admin-accounts (provider:subject) exists.')
param adminConfigured bool = false

@description('New sandboxes per day (Sandboxes:MaxNewPerDay); more sign-ups are turned away. With the weekly starter set they cost no AI.')
param maxNewSandboxesPerDay int = 5

@description('Web replicas kept running: 0 = scale to zero (cold start ~10–20 s), 1 = always warm (~$3–5 a month).')
@minValue(0)
@maxValue(1)
param webMinReplicas int = 0

@description('Data controller shown on /privacy (Operator:Name).')
param operatorName string = ''

@description('Contact e-mail shown on /privacy for data requests (Operator:Email).')
param operatorEmail string = ''

@description('"About this project" link in the footer of every page (Links:About, https only); empty = no link.')
param aboutUrl string = ''

@description('Custom domain of the web app, e.g. payments.example.com; empty = only the azurecontainerapps.io name.')
param customDomain string = ''

@description('Name of the managed certificate of the custom domain in the environment (created by infra/bind-domain.sh).')
param customDomainCertificate string = ''

var suffix = take(uniqueString(resourceGroup().id), 10)
var tags = { app: 'payment-matching-ai' }

// Built-in role definitions (public ids): Key Vault Secrets User, Key Vault Secrets Officer, Contributor.
var vaultReaderRole = '4633458b-17de-408a-b874-0445c86b69e6'
var vaultOfficerRole = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var contributorRole = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

// ---------------------------------------------------------------- identities

resource runtime 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}'
  location: location
  tags: tags
}

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-deploy'
  location: location
  tags: tags
}

// GitHub Actions signs in as the deploy identity with a short-lived OIDC token; no stored password.
resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubOidcSubject
    audiences: ['api://AzureADTokenExchange']
  }
}

resource deployerContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployer.id, contributorRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRole)
    principalId: deployer.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------- Key Vault

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv${name}${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource runtimeSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, runtime.id, vaultReaderRole)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', vaultReaderRole)
    principalId: runtime.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource operatorSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, operatorPrincipalId, vaultOfficerRole)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', vaultOfficerRole)
    principalId: operatorPrincipalId
    principalType: 'User'
  }
}

// ---------------------------------------------------------------- monitoring

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------- Container Apps

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

// Secrets are Key Vault references, read by the runtime identity; the values never pass through this template.
func kvSecret(vaultUri string, secret string, identityId string) object => {
  name: secret
  keyVaultUrl: '${vaultUri}secrets/${secret}'
  identity: identityId
}

var common = [
  { name: 'ConnectionStrings__Payments', secretRef: 'db-connection' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
  { name: 'Demo__TenantId', value: demoTenantId }
  { name: 'Ai__Budget__MonthlyUsd', value: aiMonthlyUsd }
]

var webSecrets = concat(
  [kvSecret(vault.properties.vaultUri, 'db-connection', runtime.id)],
  empty(microsoftClientId) ? [] : [kvSecret(vault.properties.vaultUri, 'microsoft-client-secret', runtime.id)],
  empty(googleClientId) ? [] : [kvSecret(vault.properties.vaultUri, 'google-client-secret', runtime.id)],
  adminConfigured ? [kvSecret(vault.properties.vaultUri, 'admin-accounts', runtime.id)] : [])

var webEnv = concat(
  common,
  [
    { name: 'Database__MigrateOnStartup', value: 'true' }
    // TLS ends at the ingress: trust X-Forwarded-Proto/-For so callbacks are https and rate limits see the client.
    { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
    { name: 'Sandboxes__MaxNewPerDay', value: string(maxNewSandboxesPerDay) }
    { name: 'Operator__Name', value: operatorName }
    { name: 'Operator__Email', value: operatorEmail }
    { name: 'Links__About', value: aboutUrl }
  ],
  empty(microsoftClientId) ? [] : [
    { name: 'Auth__Microsoft__ClientId', value: microsoftClientId }
    { name: 'Auth__Microsoft__ClientSecret', secretRef: 'microsoft-client-secret' }
  ],
  empty(googleClientId) ? [] : [
    { name: 'Auth__Google__ClientId', value: googleClientId }
    { name: 'Auth__Google__ClientSecret', secretRef: 'google-client-secret' }
  ],
  adminConfigured ? [{ name: 'Admin__Accounts__0', secretRef: 'admin-accounts' }] : [])

resource web 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: 'ca-${name}-web'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${runtime.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        customDomains: empty(customDomain) ? [] : [
          {
            name: customDomain
            bindingType: 'SniEnabled'
            certificateId: '${environment.id}/managedCertificates/${customDomainCertificate}'
          }
        ]
      }
      secrets: webSecrets
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: webEnv
        }
      ]
      scale: { minReplicas: webMinReplicas, maxReplicas: 1 }
    }
  }
  dependsOn: [runtimeSecretsUser]
}

resource job 'Microsoft.App/jobs@2024-03-01' = if (deployApps && jobEnabled) {
  name: 'caj-${name}-daily'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${runtime.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      // No retry: a run is idempotent, but a failed run should be looked at, not repeated into the budget.
      replicaRetryLimit: 0
      scheduleTriggerConfig: {
        cronExpression: jobSchedule
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        kvSecret(vault.properties.vaultUri, 'db-connection', runtime.id)
        kvSecret(vault.properties.vaultUri, 'anthropic-api-key', runtime.id)
      ]
    }
    template: {
      containers: [
        {
          name: 'job'
          image: jobImage
          args: ['--mode', 'live', '--migrate']
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(common, [{ name: 'Anthropic__ApiKey', secretRef: 'anthropic-api-key' }])
        }
      ]
    }
  }
  dependsOn: [runtimeSecretsUser]
}

output keyVaultName string = vault.name
output environmentName string = environment.name
output webAppName string = deployApps ? web.name : ''
output webFqdn string = deployApps ? web!.properties.configuration.ingress.fqdn : ''
output jobName string = deployApps && jobEnabled ? job.name : ''
output deployClientId string = deployer.properties.clientId
output environmentVerificationId string = environment.properties.customDomainConfiguration.customDomainVerificationId
