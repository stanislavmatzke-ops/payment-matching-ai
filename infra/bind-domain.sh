#!/usr/bin/env bash
# Binds a custom domain with a free managed certificate to the web app (ADR 0016). Run once, after the DNS
# records exist:
#   CNAME <sub>        -> the web app's FQDN (output webFqdn of deploy-infra.sh)
#   TXT   asuid.<sub>  -> the environment's verification id (output environmentVerificationId)
# Then put the domain and the printed certificate name into infra/main.bicepparam, so the next run of
# deploy-infra.sh keeps the binding.
#
#   infra/bind-domain.sh payments.example.com
set -euo pipefail

domain="${1:?usage: infra/bind-domain.sh <domain>}"
rg="${RG:-rg-payments}"
app=ca-payments-web
environment=cae-payments

az containerapp hostname add --resource-group "$rg" --name "$app" --hostname "$domain" --output none
az containerapp hostname bind --resource-group "$rg" --name "$app" --hostname "$domain" \
  --environment "$environment" --validation-method CNAME --output none

certificate="$(az containerapp env certificate list --resource-group "$rg" --name "$environment" --managed-certificates-only \
  --query "[?properties.subjectName=='$domain'].name | [0]" --output tsv)"
echo "Bound $domain. In infra/main.bicepparam set:"
echo "  param customDomain = '$domain'"
echo "  param customDomainCertificate = '$certificate'"
