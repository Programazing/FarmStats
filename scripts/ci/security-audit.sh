#!/usr/bin/env bash
# Audits all direct and transitive NuGet dependencies for known
# vulnerabilities using the SDK's built-in NuGet Audit feature, and fails on
# high/critical severity findings (NU1903/NU1904). Also fails if vulnerability
# data cannot be retrieved from the configured source (NU1900/NU1905).
#
# Usage: scripts/ci/security-audit.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_dir="$repo_root/FarmStats"

cd "$project_dir"

echo "==> Running NuGet Audit (restore, all severities reported, fail on high/critical): $project_dir/FarmStats.slnx"

# NuGetAudit/NuGetAuditMode/NuGetAuditLevel are forced here so CI enforces the
# same policy regardless of what's checked in to Directory.Build.props.
# NuGetAuditLevel=low reports every severity (so low/moderate findings are
# still visible in the log), but only the high/critical codes (NU1903/NU1904)
# and the "audit could not run" codes (NU1900/NU1905) are promoted to errors,
# so CI fails on the documented policy without being tripped up by low or
# moderate advisories, and a vulnerability warning cannot be silently
# absorbed by WarningsNotAsErrors.
dotnet restore "FarmStats.slnx" \
  --force \
  -p:NuGetAudit=true \
  -p:NuGetAuditMode=all \
  -p:NuGetAuditLevel=low \
  "-p:WarningsAsErrors=NU1900%3BNU1903%3BNU1904%3BNU1905" \
  -p:TreatWarningsAsErrors=false
