#!/usr/bin/env bash
# Downloads a pinned, checksum-verified Gitleaks release and scans the full
# git history reachable from the current checkout for leaked secrets.
#
# Requires: the checkout to have sufficient history (ideally `fetch-depth: 0`),
# curl, tar, and sha256sum on PATH.
#
# Usage: scripts/ci/gitleaks-scan.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

gitleaks_version="8.30.1"
# Pinned sha256 of gitleaks_8.30.1_linux_x64.tar.gz, verified against the
# project's published checksums file at
# https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_checksums.txt
gitleaks_sha256="551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb"
gitleaks_archive="gitleaks_${gitleaks_version}_linux_x64.tar.gz"
gitleaks_url="https://github.com/gitleaks/gitleaks/releases/download/v${gitleaks_version}/${gitleaks_archive}"

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

echo "==> Downloading gitleaks v${gitleaks_version}"
curl -sSfL -o "$work_dir/$gitleaks_archive" "$gitleaks_url"

echo "==> Verifying checksum"
echo "${gitleaks_sha256}  $work_dir/$gitleaks_archive" | sha256sum -c -

echo "==> Extracting gitleaks"
tar -xzf "$work_dir/$gitleaks_archive" -C "$work_dir" gitleaks
chmod +x "$work_dir/gitleaks"

report_path="${1:-$repo_root/gitleaks-report.json}"

echo "==> Scanning repository history (no-git=false, redacted output): $repo_root"
# --redact: never print secret values, only their location.
# No baseline/exceptions are applied here; keep default rules so existing
# findings are never silently hidden. --exit-code 1 (gitleaks default) fails
# the step on any finding.
"$work_dir/gitleaks" detect \
  --source "$repo_root" \
  --redact \
  --no-banner \
  --report-format json \
  --report-path "$report_path" \
  --verbose
