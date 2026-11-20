# Continuous Integration

FarmStats runs the same CI pipeline on two forges:

- GitHub Actions: `.github/workflows/ci.yml`
- Gitea Actions: `.gitea/workflows/ci.yml`

Both workflows are intentionally kept in lock-step and delegate the actual
work to small, forge-independent shell scripts in `scripts/ci/`, so platform
differences stay confined to the workflow YAML (runner selection, action
references, artifact upload) rather than leaking into the commands
themselves.

## What each workflow runs

### Triggers

- Pull requests targeting `main` (the default branch).
- Pushes to `main`.
- Pushes to `add-animal`, while this branch is still the active line of work
  and CI is being established on it. Remove this trigger once `add-animal`
  is merged, to avoid the double run described below.
- Manual `workflow_dispatch`.

Runs for the same ref are cancelled when superseded by a newer run
(`concurrency` group keyed on workflow + ref). This does **not** dedupe a
push to `add-animal` while a PR from `add-animal` into `main` is open: that
produces one `push` run and one `pull_request` run for the same commit,
because they use different refs (`refs/heads/add-animal` vs.
`refs/pull/<n>/merge`). This is accepted as a known, temporary trade-off
while `add-animal` is explicitly a push trigger.

`pull_request` (not `pull_request_target`) is used throughout, so PR runs
never execute with write-level secrets against untrusted PR code.

### Jobs

1. **`build-and-test`**
   - Checks out the source.
   - Installs the .NET SDK selected by `FarmStats/global.json` (see
     "SDK-selection policy" below - this is a feature-band roll-forward
     policy, not an exact version pin).
   - Restores local dotnet tools (`FarmStats/dotnet-tools.json`) and the
     solution (`scripts/ci/restore.sh`).
   - Checks formatting without rewriting files (`scripts/ci/format-check.sh`,
     `dotnet format --verify-no-changes`).
   - Builds the full solution in Release (`scripts/ci/build.sh`).
   - Verifies the runner actually has Docker access, then runs the full
     PostgreSQL integration-test suite in Release against disposable
     Testcontainers-managed Postgres databases (`scripts/ci/test.sh`).
   - Checks for EF Core model changes missing a migration
     (`scripts/ci/check-migrations.sh`, `dotnet ef migrations
     has-pending-model-changes`), using a syntactically valid but
     unreachable CI-only connection string (EF only needs it to build the
     `DbContext` for model comparison; it never connects, and no migration
     is ever applied).
   - Uploads test results (`.trx`) and any captured log files as a build
     artifact, even when an earlier step in the job fails.
2. **`security`**
   - Secret scan: downloads a pinned, checksum-verified Gitleaks release and
     scans the full git history reachable from the checkout
     (`scripts/ci/gitleaks-scan.sh`). Output is redacted (`--redact`); the
     JSON report is uploaded as an artifact, but it never contains raw
     secret values, only locations. The step fails on any finding,
     installation failure, or scanner error - there is no baseline and no
     `continue-on-error`.
   - Dependency audit: restores with the SDK's built-in NuGet Audit forced
     on (`NuGetAudit=true`, `NuGetAuditMode=all` so transitive packages are
     included, `NuGetAuditLevel=low` so every severity is reported in the
     log), but only promotes the high/critical codes (`NU1903`/`NU1904`)
     and the "audit could not run" codes (`NU1900`/`NU1905`) to build
     errors, so the job fails on the documented high/critical policy
     without being tripped up by low/moderate advisories
     (`scripts/ci/security-audit.sh`).
   - Neither step has access to publishing or deployment secrets.
3. **`container-build`** (depends on `build-and-test`)
   - Builds the API's deployment image with `docker build` for validation
     only - no `docker push`, no registry credentials, no `.env` file
     dependency, no embedded secrets.
   - Uses build context `FarmStats/src` with `FarmStats.API/Dockerfile`,
     matching `FarmStats/compose.yaml`'s build configuration (`context: src`,
     `dockerfile: FarmStats.API/Dockerfile`) - see "Compose and CI container
     builds" below for why that's the correct context.

## Compose and CI container builds

`FarmStats/compose.yaml`'s `farmstats.api` service and the `container-build`
CI job now use the same build context and Dockerfile path:
`context: FarmStats/src`, `dockerfile: FarmStats.API/Dockerfile`.

This was previously broken: `compose.yaml` declared `context: .` (i.e.
`FarmStats/`), but the Dockerfile's `COPY` instructions (e.g.
`COPY ["FarmStats.API/FarmStats.API.csproj", "FarmStats.API/"]`) are written
relative to a context of `FarmStats/src`, so `docker compose build` failed
with `"FarmStats.API/FarmStats.API.csproj": not found`. Fixed by changing
`compose.yaml`'s context to `src` (dockerfile path adjusted to match, since
it's now relative to the new context).

`FarmStats/.dockerignore` was moved to `FarmStats/src/.dockerignore` so it
still applies now that the build context is `FarmStats/src` (Docker looks
for `.dockerignore` at the root of the build context, not the repository
root). `FarmStats/.env` sits outside `FarmStats/src`, so it was already, and
remains, excluded from the build context entirely - verified with
`docker compose config --no-interpolate` and an actual
`docker compose build farmstats.api`.

## SDK-selection policy

`FarmStats/global.json` pins:

```json
{
  "sdk": {
    "version": "11.0.100-rc.3.26522.148",
    "rollForward": "latestFeature",
    "allowPrerelease": true
  }
}
```

This is **not** an exact version pin: `rollForward: latestFeature` tells the
.NET SDK resolver to accept the latest installed feature-band/patch release
that is `>= 11.0.100-rc.3.26522.148` within the same major.minor and SDK
feature band (the `1xx` in `100`), rather than failing if that exact build
isn't present. In practice this means a newer `11.0.1xx` preview/RC/RTM SDK
installed on a runner will be used instead of failing outright, while an SDK
from a different major/minor or an older one is rejected.

.NET resolves `global.json` by searching the current working directory and
its parent directories - **not** by the path of the project/solution passed
on the command line. Every script in `scripts/ci/` now explicitly `cd`s into
`FarmStats/` (where `global.json` lives) before invoking any `dotnet`
command, and then uses solution/project-relative paths from there. This was
previously missing: the scripts computed an absolute path to
`FarmStats/FarmStats.slnx` but invoked `dotnet` from the repository root,
so `global.json` was never actually discovered or honored - it happened to
produce correct results locally only because the newest SDK already
installed (`11.0.100-rc.3.26522.148`) coincided with the pinned version.
Both workflows additionally pass `global-json-file: FarmStats/global.json`
to `actions/setup-dotnet`, so the hosted runner installs a matching SDK
before any script runs.

## Running the equivalent commands locally

All commands assume you're at the repository root (the directory containing
`scripts/`), and that Docker is running locally.

```bash
scripts/ci/restore.sh           # dotnet tool restore + dotnet restore
scripts/ci/format-check.sh      # dotnet format --verify-no-changes
scripts/ci/build.sh             # dotnet build -c Release --no-restore
scripts/ci/test.sh ./test-results   # full Testcontainers integration suite
scripts/ci/check-migrations.sh  # dotnet ef migrations has-pending-model-changes
scripts/ci/security-audit.sh    # dotnet restore with NuGet Audit enforced
scripts/ci/gitleaks-scan.sh ./gitleaks-report.json  # secret scan
```

## GitHub runner prerequisites

- `ubuntu-latest` GitHub-hosted runners, which ship with a usable .NET SDK
  install path, Docker CLI, and a running Docker daemon out of the box -
  verified locally against the same Testcontainers setup these jobs use.
- Outbound network access to `github.com` (pinned actions), `nuget.org`
  (NuGet Audit / restore), `mcr.microsoft.com` (base images used by the
  Dockerfile and the SDK Docker images Testcontainers pulls), and
  `github.com/gitleaks/gitleaks` releases (secret scan).
- No special runner configuration is required beyond the default
  GitHub-hosted runner.

## Gitea runner setup and security requirements

**Everything in this section is an assumption pending verification on the
actual Gitea server; it has not been confirmed against a real
`act_runner`.**

- Assumes an `act_runner` is registered with the label `ubuntu-latest`. This
  label is **not** guaranteed to exist or to imply Docker-in-Docker
  capability on your instance - it must be confirmed (or replaced with
  the actual registered label) before this workflow can be expected to run
  at all.
- Docker access for Testcontainers and the `container-build` job's own
  `docker build`: `act_runner` must be configured so jobs can reach a
  Docker daemon (typically Docker-in-Docker, `act_runner`'s own `docker`
  execution mode, or a daemon reachable over a configured socket/TCP
  endpoint). The `build-and-test` job fails fast with an actionable
  `::error::` message if `docker info` doesn't succeed, rather than letting
  Testcontainers time out with an unclear error.
- Do **not** grant jobs access to a production Docker host, and do **not**
  mount the host's Docker socket into an untrusted or long-lived runner
  without understanding the security implications (a job with Docker socket
  access can generally escape its container). Prefer an isolated
  Docker-in-Docker setup dedicated to CI.
- Networking: Testcontainers publishes the ephemeral Postgres container on a
  dynamically chosen host port and connects back to it from inside the job.
  The runner's container network must allow the job to reach that published
  port on the Docker host it's using (this works by default with a
  conventional Docker-in-Docker or host-Docker-socket setup, but may need
  explicit configuration with a remote/TCP Docker daemon).
  - Outbound network access equivalent to the GitHub prerequisites above.
- `actions/checkout`, `actions/setup-dotnet`, and `actions/upload-artifact`
  are referenced by the same pinned commit SHAs as the GitHub workflow.
  Gitea's Actions implementation is designed to be source-compatible with
  these actions, but whether your instance can actually resolve them
  (directly from `github.com`, through a configured mirror, or not at all)
  depends on server configuration and must be confirmed. If they don't
  resolve, these steps need to be replaced with manual installation steps
  (e.g. `dotnet` is usually already on the runner image; artifacts can be
  uploaded with Gitea's own artifact action if available, or skipped).
- `workflow_dispatch` requires Actions support for manual triggers; this has
  shipped in Gitea for a while but should be confirmed against the
  installed version.

None of this has been exercised against a live Gitea runner as part of this
change - the Gitea workflow has only been validated locally (YAML syntax and
`actionlint`), not by an actual hosted run. Treat it as pending server-side
verification until it has run successfully on your `act_runner`.

## Branch protection

CI does not configure branch protection; that must be set up separately in
each forge's settings, selecting the exact job names below as required
status checks:

- GitHub: Settings → Branches → branch protection rule for `main` → required
  status checks: `Build and test`, `Security (secrets + dependency audit)`,
  `Container build (no publish)`.
- Gitea: Repository Settings → Branches → branch protection for `main` →
  required status checks, using the same job names, once you've confirmed
  the workflow actually runs on your instance (status checks can't be
  selected before Gitea has seen at least one run report them).

## Limitations

These checks validate the application in isolation (build, format,
migrations, tests, secrets, dependency vulnerabilities, and that the
container image builds). They do **not** fix, detect, or protect against:

- Cross-entity races in the application's own concurrency-control logic
  (see the existing concurrency/optimistic-locking tests under
  `FarmStats/tests`) - CI only re-runs those tests as written, it adds no
  new coverage for races beyond what already exists.
- Runtime/production configuration issues, since the test suite and
  migration check use CI-only, disposable connection strings and never
  touch a development, staging, or production database.
