#!/usr/bin/env bash
# Deploys infra/main.bicep with the settings in infra/main.bicepparam (ADR 0016).
# Run by a person with Owner on the resource group (it assigns roles), after `az login`.
# Keeps the images that run now; a first deployment uses the images of the current commit,
# which the Deploy workflow pushed to ghcr.io.
#
#   RG=rg-payments LOCATION=germanywestcentral infra/deploy-infra.sh
set -euo pipefail

rg="${RG:-rg-payments}"
location="${LOCATION:-germanywestcentral}"
registry="ghcr.io/stanislavmatzke-ops"
here="$(cd "$(dirname "$0")" && pwd)"

az group create --name "$rg" --location "$location" --tags app=payment-matching-ai --output none

current() { az "$@" --query "properties.template.containers[0].image" --output tsv 2>/dev/null || true; }
tag="sha-$(git -C "$here" rev-parse HEAD)"
web_image="$(current containerapp show --resource-group "$rg" --name ca-payments-web)"
job_image="$(current containerapp job show --resource-group "$rg" --name caj-payments-daily)"
web_image="${web_image:-$registry/payments-web:$tag}"
job_image="${job_image:-$registry/payments-job:$tag}"
operator="$(az ad signed-in-user show --query id --output tsv)"

echo "Deploying to $rg: web $web_image, job $job_image"
az deployment group create \
  --resource-group "$rg" \
  --name "payments-$(date -u +%Y%m%d%H%M%S)" \
  --parameters "$here/main.bicepparam" \
  --parameters operatorPrincipalId="$operator" webImage="$web_image" jobImage="$job_image" \
  --query "properties.outputs" --output json
