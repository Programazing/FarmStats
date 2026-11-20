#!/usr/bin/env bash
# Builds the full solution in Release configuration.
# Usage: scripts/ci/build.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"

cd "$project_dir"

echo "==> Building solution (Release): $project_dir/FarmStats.slnx"
dotnet build "FarmStats.slnx" --configuration Release --no-restore
