#!/usr/bin/env bash
# Restores local dotnet tools and the solution's NuGet packages.
# Usage: scripts/ci/restore.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"

cd "$project_dir"

echo "==> Restoring local dotnet tools (manifest: $project_dir/dotnet-tools.json)"
dotnet tool restore --tool-manifest "dotnet-tools.json"

echo "==> Restoring solution: $project_dir/FarmStats.slnx"
dotnet restore "FarmStats.slnx"
