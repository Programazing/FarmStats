# FarmStats agent instructions

## Purpose and authority

This repository is Programazing/FarmStats. Follow the user's current task and these repository instructions. Treat source code and maintained documentation as evidence of current behavior; distinguish them from proposed requirements. If instructions conflict or facts cannot be verified, report the conflict rather than guessing.

## Verify the workspace first

Before investigation or changes, report:

```sh
pwd
git rev-parse --show-toplevel
git remote -v
git branch --show-current
git status --short --untracked-files=all
```

- Verify that origin identifies Programazing/FarmStats (SSH or HTTPS).
- If this is another repository, stop. Do not inspect unrelated projects or search the whole filesystem.
- If branch or working-tree state differs from the task's prerequisites, stop and report it. A dirty tree is not permission to clean it.
- Preserve unrelated work. Never discard, reset, stash, stage, or modify it without explicit authorization.
- Inspect existing repository and applicable parent/directory-specific agent instructions before changing this file or starting implementation.

## Scope and permissions

- Planning and repository assessment are read-only unless changes are explicitly requested.
- A read-only assessment does not authorize starting services, containers, builds, tests, migrations, or HTTP workflows.
- Editing authorization is not authorization to commit, push, merge, delete branches, or change remote state. Obtain explicit authorization for those actions.
- Never apply migrations to a developer or production database without explicit authorization and verification of the target.
- Never run destructive database resets or use development data as disposable fixtures.
- Do not expand a narrow task into unrelated cleanup, new features, or architectural rewrites.
- Do not create temporary review reports, agent skills, or memory artifacts in the repository unless requested.
- Do not launch duplicate or background verification runs unless requested. Report a timeout and investigate before retrying.

## Project map

Paths below are relative to the Git repository root:

- `FarmStats/`: solution directory; expected solution file is `FarmStats.slnx`.
- `FarmStats/src/FarmStats.API/`: API, persistence, migrations, and feature endpoints.
- `FarmStats/tests/FarmStats.API.IntegrationTests/`: PostgreSQL integration tests.
- `FarmStats/README.md`: development setup and application conventions.
- `docs/animal-tracking.md`: individual/group tracking and legacy classification.
- `docs/animal-quantity-events.md`: quantity-event contract and limitations.
- `docs/ci.md`: verification pipelines and prerequisites.
- `docs/roadmap.md`: completed foundations, proposals, and unresolved decisions.

Verify these paths against the checkout. Do not infer nonexistent files or contracts from this overview.

## Domain invariants

Preserve these established decisions unless the user explicitly approves a change:

- Animal records explicitly represent individuals or groups, including named batches. Names and quantities do not determine tracking type.
- Individuals have quantity one. A group with one remaining animal is still a group.
- A group can reach zero without automatic lifecycle changes or archiving.
- Existing legacy `Unspecified` records are not silently classified from quantity.
- Already classified records do not switch individual/group type through ordinary edits.
- Explicit group quantity changes use the quantity-event workflow: additions, deaths, sales, and corrections. Ordinary edits must not bypass it.
- Whole-record lifecycle changes and partial group quantity events have different meanings.
- Lifecycle, acquisition, reproductive condition, location, archiving, and soft deletion are separate concerns; do not collapse them into one status enum.
- Soft deletion is not a substitute for recording a sale or death.
- Preserve revision-based concurrency, atomic state/history writes, and append-only audit history.
- Do not fabricate historical events, acquisition dates, or classifications for existing data.
- Backdated quantity events affect the current count; do not claim the existing trail reconstructs historical balances.
- Splitting, merging, group transfers, and individual extraction require explicit designs, not implicit conversion through PUT.

Read the feature documentation and implementation for exact validation, enum values, responses, and compatibility behavior.

## Implementation rules

- Inspect existing endpoint, validation, persistence, history, concurrency, and test patterns before adding alternatives.
- Keep changes narrowly scoped; avoid a generic repository layer or broad rewrites without approval.
- Preserve existing enum numeric values and migration history.
- Add new migrations when needed; do not squash, remove, or rewrite old migrations without explicit authorization.
- Never silently delete, merge, or rename conflicting existing data to make a migration pass. Provide diagnostics and an explicit resolution path.
- Keep secrets out of source, logs, prompts, and committed configuration. Follow the documented User Secrets/environment setup.
- Update relevant API descriptions, HTTP examples, tests, and maintained documentation when contracts change.
- Distinguish same-record revision protection from cross-entity integrity. Do not claim revision tokens or foreign keys solve soft-deletion assignment races.
- Do not assume anonymous endpoints are deployment-ready or that broad query-filter bypasses are safe for future farm scoping.

## Verification

For an authorized implementation task, inspect `docs/ci.md`, the committed SDK/tool configuration, and existing CI scripts for exact current commands and prerequisites. Prefer those maintained commands over inventing replacements.

Common solution-level checks, run from `FarmStats/`, include:

```sh
dotnet tool restore
dotnet restore FarmStats.slnx
dotnet format FarmStats.slnx --verify-no-changes --no-restore
dotnet build FarmStats.slnx --configuration Release --no-restore
dotnet test FarmStats.slnx --configuration Release --no-build --no-restore
dotnet ef migrations has-pending-model-changes --project src/FarmStats.API --configuration Release --no-build -- --environment Development
```

Verify tool-manifest discovery and configuration before execution. Do not start services automatically just because a command needs them. If a prerequisite is missing, report it and request authorization where necessary.

- Integration tests must use isolated disposable PostgreSQL databases, not developer records.
- EF consistency checks may need context configuration; never supply a real production connection.
- Manual HTTP workflows write to the API's configured database; they are not isolated integration tests. Execute only when authorized and use their disposable fixtures.
- Report formatting failures before applying formatting changes outside the task scope.
- A historical test count is not a permanent gate. Verify discovery and actual results of the intended suite.
- Documentation-only changes normally need diff and link/path review, not builds or database tests, unless specifically requested.

## Reporting and handoff

Report:

1. Repository, branch, and initial working-tree state.
2. Files changed and major behavior or contract changes.
3. Exact verification commands and outcomes: passed, failed, timed out, or not run.
4. Remaining risks, blockers, and approval needed next.

Distinguish source observations, executed checks, prior user-reported results, and proposals. Never present an old test result as a new run. Never claim remote issues, PRs, checks, or project boards were inspected without actually inspecting them.

Do not call a proposed roadmap item an approved requirement. Do not automatically select or implement the next feature after completing the current task.
