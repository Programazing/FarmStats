# FarmStats: individual and group animal tracking

## Base and scope

Prepared from the inspected Programazing/FarmStats dev branch. No GitHub writes,
commits, or pushes were performed. This package contains one Git patch: it creates
9 files and updates 12 existing files. There is NO Python installer.

This first step adds TrackingType, quantity rules, response fields, persistence
constraints, migration/snapshot changes, PostgreSQL tests, and a Rider HTTP workflow.
It does not implement quantity-event history, splitting/merging, extracting an
individual from a group, Mixed sex, or a lifecycle redesign.

## 1. Create a branch

From the existing repository ROOT (the folder containing .git):

    git status --short

If that shows changes, stop and preserve them before switching branches.
Assuming origin is the GitHub remote for this repository:

    git fetch origin
    git switch --no-track -c feature/animal-tracking origin/dev

This starts directly from the fetched dev branch. It does not require a local dev
branch and does not change the currently checked-out branch on the remote server.

## 2. Apply the files

Extract this ZIP into ~/Downloads/AnimalTracking. Leave it there.
Keep your terminal in the existing repository ROOT, NOT in Downloads or the API:

    git apply --check --unidiff-zero "$HOME/Downloads/AnimalTracking/animal-tracking.patch"

If the check succeeds:

    git apply --unidiff-zero "$HOME/Downloads/AnimalTracking/animal-tracking.patch"
    git diff --stat
    git status --short

The patch contains exact source-block replacements, hence --unidiff-zero.
It updates the existing checkout and creates new files at their correct paths.
Do not copy the extracted folder into your repository. Do not apply the patch twice.
If --check fails, stop: dev or your local source differs. Do not use --reject or
force application. Supply the error and affected file before proceeding.

## 3. Behavior

TrackingType numeric values:
- 0 Unspecified: existing records awaiting deliberate classification.
- 1 Individual: exactly one animal.
- 2 Group: a batch/flock/group, including a group with one remaining member.

Creation:
- Omitted TrackingType defaults to Individual for compatibility with current
  single-animal clients. No type is ever inferred from Quantity.
- Groups must explicitly send trackingType: 2.
- Initial quantity must be positive; individuals must have quantity 1.
- Unspecified and undefined enum values are rejected by the creation API.

Update:
- Revision remains required; existing stale-revision handling is preserved.
- Quantity must be present and non-null.
- Individuals must keep quantity 1.
- Groups may reach quantity 0 without changing their type or lifecycle status.
- Omitted/null TrackingType preserves the stored type. This is a documented
  exception to optional detail-field clearing, not an accidental PUT change.
- Existing Unspecified records may be classified as Individual or Group once.
- A classified record cannot switch type through ordinary PUT.
- Existing optional detail-field clearing and lookup validation remain intact.

Names such as Bessie, Flock of hens, or Batch 1028 are ordinary user-defined names.
No naming convention or name uniqueness is introduced.

Creation/update/detail/list and lifecycle mutation responses include TrackingType.
Lifecycle mutation responses also include Quantity. Existing history entries are
not relabelled as historical tracking-type snapshots.

## 4. Existing data and migration

The migration adds TrackingType=0 to EVERY existing record, including deleted ones.
It does not guess Individual from quantity 1, infer Group from larger counts,
change quantities, rotate revisions, or invent history.

Run/review docs/animal-tracking-preflight.sql BEFORE applying to a non-disposable DB.
Any existing quantity < 1 must be resolved deliberately; it would block the new
check constraint. No automatic repair is included.

After migration, review unclassified records:

    SELECT "Id", "Name", "Quantity", "DeletedAtUtc"
    FROM "Animals" WHERE "TrackingType" = 0 ORDER BY "Name", "Id";

Classify through PUT with the latest revision and all ordinary required fields.
For deleted records, restore/review through the existing lifecycle flow before
editing; this package does not introduce a privileged bulk classification tool.

Included migration (authored here, NOT generated or executed by EF here):
20261120034000_AddAnimalTrackingType

It includes its fixed target-model designer and the updated model snapshot.
Do NOT run 'migrations add' for the same change: the migration is already supplied.
The database check enforces valid type/quantity combinations. Tracked saves also
prevent changing an already classified type. Raw SQL/bulk writes can still bypass
tracked immutability checks; type-conversion prevention is not a database trigger.
The legacy value 0 remains accepted at persistence level for existing records.

Downgrading drops the tracking classification; zero-quantity groups may also be
incompatible with older application validation. Do not downgrade without review.

## 5. Build and verification

From the repository root, restore, format, then build/test. Formatting is included
because the new authored files have not been run through your local SDK formatter.
The formatting command can modify touched files; review the diff afterward.

    cd FarmStats
    dotnet tool restore
    dotnet restore FarmStats.slnx
    dotnet format FarmStats.slnx --no-restore
    dotnet build FarmStats.slnx --configuration Release --no-restore
    dotnet test FarmStats.slnx --configuration Release --no-build --no-restore
    dotnet ef migrations has-pending-model-changes --project src/FarmStats.API --configuration Release --no-build -- --environment Development

If the tool manifest is not auto-discovered in your local setup, use the repository's
scripts/ci/restore.sh from the repository root for tool and solution restoration.
Tests require Docker for isolated PostgreSQL. They do not use your development DB.
The new test files add 20 cases on top of the existing suite, including migration
preservation of both quantity-one and quantity-many legacy records.

Before updating your actual development database, inspect the migration and run
the preflight. Then, with your existing Development connection configured:

    dotnet ef database update --project src/FarmStats.API --configuration Release --no-build -- --environment Development

Do NOT run database update if build, tests, or model-consistency verification fail.
An exported ConnectionStrings__FarmStats overrides User Secrets: verify its target.
Rebuild/restart the API container after updating the development database.

The optional manual workflow is:
FarmStats/src/FarmStats.API/Features/Animal/Tracking/AnimalTracking.http
It creates and later soft-deletes its own group; it does not modify Bessie.

## 6. Current limitations

Group quantity is still editable through PUT, matching the existing API architecture.
This step adds no reasoned quantity-change ledger. A change from 150 to 145 cannot
yet distinguish sale, death, transfer, or count correction. Do not present existing
lifecycle history as a complete population ledger.

A group quantity of zero does not imply death, sale, or automatic archiving.
Whole-record lifecycle commands retain their existing semantics; partial group
events need a separate follow-up design. Groups may be mixed-age/mixed-sex in the
real world, but this patch does not invent new values for the existing fields.

The documented cross-entity lookup-assignment/deletion races are not fixed here.
Anonymous development endpoints and future farm-scoping requirements are unchanged.

## Verification performed here

Patch syntax and exact-block transformation checks passed against reconstructed
source fragments taken from the inspected dev files. ZIP integrity was checked.
This is NOT a clean-clone/full-repository patch test. --check in your checkout is
mandatory. No .NET compilation, EF model comparison, PostgreSQL migration, or
integration test was executed here; run the commands above before committing.
