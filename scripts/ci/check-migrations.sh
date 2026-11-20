#!/usr/bin/env bash
# Fails if the EF Core model has changes that are not captured by a migration.
# Uses a syntactically valid but unreachable connection string: EF only needs
# it to build the DbContext for model comparison, it never has to connect.
# Does not apply migrations to any database.
#
# Usage: scripts/ci/check-migrations.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"
api_project="$project_dir/src/FarmStats.API/FarmStats.API.csproj"

echo "==> Checking for model changes missing a migration: $api_project"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export ConnectionStrings__FarmStats="${ConnectionStrings__FarmStats:-Host=localhost;Port=1;Database=ci-design-time;Username=ci;Password=ci}"

(cd "$project_dir" && dotnet tool run dotnet-ef -- migrations has-pending-model-changes \
  --project "$api_project" \
  --startup-project "$api_project")
