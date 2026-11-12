#!/usr/bin/env bash
# Builds the legacy Delphi module with Free Pascal (in Docker) and runs it.
#
# Usage: legacy/run-legacy.sh <input-dir> <output-file>
#   Paths are relative to the repository root, e.g.
#   legacy/run-legacy.sh data/sample legacy/golden/seed-42/legacy_matches.csv
set -euo pipefail

FPC_IMAGE="freepascal/fpc:3.2.2-full"

if [ $# -ne 2 ]; then
  echo "Usage: $0 <input-dir> <output-file>" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
# Git Bash on Windows: Docker needs a Windows path and no MSYS path conversion.
host_root="$(cd "$repo_root" && (pwd -W 2>/dev/null || pwd))"
export MSYS_NO_PATHCONV=1

docker run --rm \
  -v "$host_root:/repo" -w /repo \
  -e IN_DIR="$1" -e OUT_FILE="$2" \
  "$FPC_IMAGE" sh -c '
    set -e
    mkdir -p legacy/delphi/bin "$(dirname "$OUT_FILE")"
    fpc -Mdelphi -O2 -vew -FUlegacy/delphi/bin -FElegacy/delphi/bin legacy/delphi/PayMatch.dpr > legacy/delphi/bin/build.log 2>&1 \
      || { cat legacy/delphi/bin/build.log; exit 1; }
    legacy/delphi/bin/PayMatch "$IN_DIR" "$OUT_FILE"
  '
