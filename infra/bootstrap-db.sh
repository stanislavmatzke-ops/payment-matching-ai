#!/usr/bin/env bash
# One-off: creates the database `payments` and its own login role `payments_app` (owner of that database
# only, no admin rights) on an existing PostgreSQL Flexible Server, and stores the app's connection string
# as the Key Vault secret `db-connection` (ADR 0016). Safe to run again: it only resets the role's password.
#
# Needs: az login (read the admin connection string, change the server firewall, write the secret), Docker
# (psql from postgres:17-alpine). Nothing secret is printed or written to the repository; a temporary file
# with the new connection string lives in a private temp directory and is deleted on exit, together with
# the temporary firewall rule for this machine's address.
#
#   TARGET_VAULT=<kv from deploy-infra.sh> infra/bootstrap-db.sh
set -euo pipefail

server_rg="${SERVER_RG:-rg-jobscout}"
server="${SERVER:-psql-jobscout-yeek5j}"
source_vault="${SOURCE_VAULT:-kv-jobscout-yeek5j}"
source_secret="${SOURCE_SECRET:-db-connection}"
target_vault="${TARGET_VAULT:?set TARGET_VAULT to the payments Key Vault}"
database=payments
role=payments_app

rule="bootstrap-payments-$(date -u +%Y%m%d%H%M%S)"
tmp="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp"
  az postgres flexible-server firewall-rule delete --resource-group "$server_rg" --server-name "$server" \
    --name "$rule" --yes --output none 2>/dev/null || true
}
trap cleanup EXIT

host="$(az postgres flexible-server show --resource-group "$server_rg" --name "$server" --query fullyQualifiedDomainName --output tsv)"

# The admin connection string (Npgsql format) -> PG* variables for psql; never echoed.
admin="$(az keyvault secret show --vault-name "$source_vault" --name "$source_secret" --query value --output tsv)"
PGUSER=""; PGPASSWORD=""
IFS=';' read -ra parts <<< "$admin"
for part in "${parts[@]}"; do
  key="$(printf '%s' "${part%%=*}" | tr '[:upper:]' '[:lower:]' | tr -d ' ')"
  value="${part#*=}"
  case "$key" in
    username|userid|user) PGUSER="$value" ;;
    password) PGPASSWORD="$value" ;;
  esac
done
unset admin parts
[[ -n "$PGUSER" && -n "$PGPASSWORD" ]] || { echo "Could not read user and password from $source_vault/$source_secret." >&2; exit 1; }
export PGUSER PGPASSWORD PGHOST="$host" PGDATABASE=postgres PGSSLMODE=require

ip="$(curl -sSf https://api.ipify.org)"
echo "Temporary firewall rule $rule for this machine on $server"
az postgres flexible-server firewall-rule create --resource-group "$server_rg" --server-name "$server" \
  --name "$rule" --start-ip-address "$ip" --end-ip-address "$ip" --output none

APP_PASSWORD="$(openssl rand -hex 24)"
export APP_PASSWORD

echo "Creating role $role and database $database (if missing)"
docker run --rm -i -e PGUSER -e PGPASSWORD -e PGHOST -e PGDATABASE -e PGSSLMODE -e APP_PASSWORD \
  postgres:17-alpine psql --no-psqlrc --quiet -v ON_ERROR_STOP=1 <<SQL
\getenv app_password APP_PASSWORD
SELECT 'CREATE ROLE $role LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$role') \gexec
ALTER ROLE $role PASSWORD :'app_password';
-- PostgreSQL 16: the admin may give a database to a role only when it can SET ROLE to it.
GRANT $role TO CURRENT_USER WITH SET TRUE;
SELECT 'CREATE DATABASE $database OWNER $role' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$database') \gexec
REVOKE CONNECT, TEMPORARY ON DATABASE $database FROM PUBLIC;
SQL

conn="Host=$host;Port=5432;Database=$database;Username=$role;Password=$APP_PASSWORD;Ssl Mode=Require"
printf '%s' "$conn" > "$tmp/db-connection"
unset conn APP_PASSWORD
az keyvault secret set --vault-name "$target_vault" --name db-connection --file "$tmp/db-connection" --output none
echo "Stored $target_vault/db-connection. Firewall rule $rule is removed on exit."
