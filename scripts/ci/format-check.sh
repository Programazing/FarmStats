#!/usr/bin/env bash
# Verifies formatting without rewriting any files.
# Usage: scripts/ci/format-check.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"

cd "$project_dir"

echo "==> Checking formatting: $project_dir/FarmStats.slnx"
dotnet format "FarmStats.slnx" --verify-no-changes
