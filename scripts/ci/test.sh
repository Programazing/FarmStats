#!/usr/bin/env bash
# Runs the full PostgreSQL (Testcontainers-backed) integration test suite in
# Release configuration. Requires working Docker access: each test spins up
# its own disposable postgres:17-alpine container, there is no shared
# database and no fallback connection string is ever used.
#
# Usage: scripts/ci/test.sh <results-directory>
set -euo pipefail

results_dir="${1:?Usage: test.sh <results-directory>}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"
test_project="$project_dir/tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj"

echo "==> Verifying Docker is reachable (required for Testcontainers)"
if ! docker info > /dev/null 2>&1; then
  echo "ERROR: Docker is not reachable from this runner." >&2
  echo "The integration test suite uses Testcontainers to start disposable" >&2
  echo "PostgreSQL containers per test and cannot run without Docker access." >&2
  exit 1
fi

mkdir -p "$results_dir"

cd "$project_dir"

echo "==> Running integration tests (Release): $test_project"
dotnet test "tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj" \
  --configuration Release \
  --no-build \
  --no-restore \
  --logger "trx;LogFileName=integration-tests.trx" \
  --logger "console;verbosity=normal" \
  --results-directory "$results_dir"
