# FarmStats Code Review

**Reviewed commit:** `e94c1ad` (HEAD of `feature/group-quantity-events` branch)
**Working tree:** Includes staged and unstaged changes for the group-quantity-events feature
**Date:** 2025-11-25
**Reviewer:** Ivy (Hermes Agent)

---

## A. Review Scope and Project Map

### Repository State
- **Branch:** `feature/group-quantity-events` (merged from `feature/animal-tracking`)
- **HEAD:** `e94c1ad` — Merge pull request #2
- **Working tree:** 3 staged new files, 11 modified tracked files, 2 untracked migration files
- **Scope:** This review covers the entire working tree (committed + uncommitted changes)

### Architecture Overview
FarmStats is a .NET 11, FastEndpoints, EF Core + PostgreSQL farm-management API. The domain models **animals** (individually tracked or as groups/batches) with **lifecycle events** (status changes, soft deletion, restoration) and **quantity events** (additions, deaths, sales, corrections) for group-tracked animals. All mutations are audit-logged in `AnimalHistory`.

**Key entities:**
- `Animal` — core aggregate; `TrackingType` (Unspecified/Individual/Group), `Quantity`, `Status`, soft-delete fields, `Revision` (optimistic concurrency token)
- `AnimalHistoryEntry` — append-only audit log; discriminates `EventType` (Created, StatusChanged, Deleted, Restored, QuantityChanged) with type-specific payload columns
- `Species`/`Breed`/`FarmArea` — soft-deletable lookups with their own `LookupHistoryEntry` audit trail

**Request-to-database paths:**
1. **Create animal** → validates lookups → adds `Animal` + `AnimalHistoryEntry(Created)` → `SaveChanges` (sets `Revision`)
2. **Update animal** → loads by ID → checks `Revision` → validates tracking/quantity rules → updates fields → sets new `Revision` → `SaveChanges`
3. **Change status** → loads → checks `Revision` → adds `AnimalHistoryEntry(StatusChanged)` → updates `Animal.Status*` → `SaveChanges`
4. **Record quantity event** (group only) → loads → checks `Revision` + `TrackingType==Group` + `Status==Active` → computes new quantity → adds `AnimalHistoryEntry(QuantityChanged)` + updates `Animal.Quantity` → `SaveChanges`
5. **Soft delete** → loads → checks `Revision` → adds `AnimalHistoryEntry(Deleted)` → sets `DeletedAtUtc` → `SaveChanges`
6. **Restore** → loads ignoring query filter → checks `Revision` → adds `AnimalHistoryEntry(Restored)` → clears delete fields → `SaveChanges`

**Persistence configuration:**
- Global query filter: `Animal.DeletedAtUtc == null`
- `Revision` mapped as concurrency token on `Animal`, `Species`, `Breed`, `FarmArea`
- Database check constraint `CK_Animals_TrackingType_Quantity` enforces tracking/quantity rules
- Database check constraint `CK_AnimalHistory_QuantityEvent` enforces quantity-event history shape
- Unique indexes on normalized lookup names (`upper(btrim(Name))`) including deleted rows
- All FKs `ON DELETE RESTRICT`; no cascades

**Test infrastructure:** xUnit + Testcontainers (PostgreSQL 17-alpine), `WebApplicationFactory`, isolated container per test class. Integration tests cover animal tracking, quantity events, concurrency, history/restore, lookup lifecycle, migrations, and duplicate-upgrade scenarios.

---

## B. Overall Assessment

**What works well:**
- Clean, consistent FastEndpoints architecture with request/response DTOs, validators, and OpenAPI summaries
- Strong domain modeling: explicit `TrackingType` enum, quantity rules enforced at both application and database level
- Audit trail is append-only by design (EF `SaveChanges` interceptors reject history modifications)
- Optimistic concurrency via `Guid Revision` on all mutable entities; tested under simultaneous writes
- Soft deletion preserves history and blocks lookup deletion while references (including soft-deleted) exist
- Migration-driven schema with check constraints and unique indexes that survive raw SQL bypasses
- Comprehensive integration test suite using real PostgreSQL in containers; tests verify behavior not just happy paths
- Explicit documentation of known concurrency limitations in README (cross-entity race conditions)

**Most important risks:**
1. **Cross-entity concurrency gap** (documented in README): Animal create/update validates active lookups, but concurrent lookup deletion can succeed between validation and save, leaving animals pointing to soft-deleted lookups. This is a real data-integrity risk for any multi-user deployment.
2. **Missing `Npgsql` package reference** in test project — integration tests cannot build/run without manual fix.
3. **`ActorUserId` never populated** — audit trail lacks accountability; reserved for future auth but no placeholder implementation.
4. **`UpdateAnimal` requires full payload** — all fields must be sent (even unchanged ones) or they are cleared; no partial-update/PATCH support.
5. **No authentication/authorization** — all endpoints `AllowAnonymous()`; README acknowledges this is dev-only but no migration path defined.

**Recommendation:** Fix the test project build blocker first, then address the cross-entity concurrency gap if/when moving beyond single-user dev. The quantity-events feature is well-implemented and tested; the core domain logic is solid.

---

## C. Findings (Ordered by Severity)

### CRITICAL

#### F-001: Test project missing `Npgsql` package — integration tests cannot build
- **File:** `tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj` (lines 9–18)
- **Behavior:** Four test files import `Npgsql` (`using Npgsql;` and `PostgresException`) but the project lacks `<PackageReference Include="Npgsql" />`.
- **Failure scenario:** `dotnet build tests/...` fails with `CS0246: The type or namespace name 'Npgsql' could not be found`.
- **Why it matters:** CI cannot run integration tests; developers cannot verify changes locally.
- **Correction:** Add `<PackageReference Include="Npgsql" Version="9.0.3" />` (or version compatible with EF Core 10.0.15).
- **Regression test:** `dotnet build tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj` succeeds; `dotnet test` runs all integration tests.

---

### HIGH

#### F-002: Cross-entity concurrency race between animal writes and lookup deletion
- **File:** `README.md` (lines 20–24), `FarmStatsDbContext.AnimalTracking.cs` (lines 18–35), `LookupLifecycleSupport.cs` (lines 33–49)
- **Behavior:** Animal create/update checks `Species`/`Breed`/`Area` existence via `AnyAsync` (ignoring soft-delete filter for breed/species validation). Lookup deletion checks `HasReferences` which includes soft-deleted animals. Between the animal's validation read and its `SaveChanges`, a concurrent deletion can soft-delete the lookup. The animal then saves with a FK to a deleted lookup.
- **Failure scenario:**
  1. Request A: `PUT /animal/{id}` validates `SpeciesId` exists (active).
  2. Request B: `POST /species/{speciesId}/deletion` sees no references (animal A not yet saved), soft-deletes species.
  3. Request A: `SaveChanges` succeeds — animal now references deleted species.
- **Why it matters:** Referential integrity violated at application level; FK only checks row existence, not `DeletedAtUtc`. Documented as acceptable only for "development branch with controlled writes."
- **Correction (smallest):** Add row-level locking protocol: before validating a lookup, `SELECT ... FOR SHARE` (or `FOR NO KEY UPDATE`) on the lookup row; deletion must acquire `FOR UPDATE` on the same row in consistent order. Or use serializable transactions with retry. This requires coordination across all writers (animal, breed, species, area, seeding).
- **Regression test:** `ConcurrentMutationTests` should add a test where simultaneous animal-create and species-deletion either both succeed with consistent state or one fails with 409.

#### F-003: `ActorUserId` never populated — audit trail lacks accountability
- **File:** `AnimalHistoryEntry.cs` (line 33), `LookupHistoryEntry.cs` (line 26), all endpoint handlers
- **Behavior:** `ActorUserId` property exists on both history entities with comment "Reserved for authenticated, server-derived identity. Never populate this from a request-body user ID." No endpoint sets it.
- **Failure scenario:** Any mutation records history with `ActorUserId = null`. Cannot trace *who* made a change.
- **Why it matters:** Audit trail is incomplete; cannot satisfy compliance or debugging needs requiring actor identification.
- **Correction:** When authentication is added, populate from `HttpContext.User` (e.g., `sub` claim). For now, populate with a placeholder like `"anonymous"` or `"dev"` to make the field non-null and the gap visible.
- **Regression test:** Integration test asserts `ActorUserId != null` on every history entry after any write.

---

### MEDIUM

#### F-004: `UpdateAnimal` requires full payload — no partial update support
- **File:** `UpdateAnimal.cs` (lines 155–164, 208–237), `README.md` (line 26)
- **Behavior:** PUT replaces all fields; omitted/null optional fields are cleared (e.g., `BreedId`, `AreaId`, `ReferenceId`, `Color`, `Birthdate`). Client must send current values for all fields to avoid data loss.
- **Failure scenario:** Client sends `{ "revision": "...", "name": "NewName" }` → `BreedId`, `AreaId`, etc. become `null`.
- **Why it matters:** Violates principle of least surprise for REST APIs; increases client complexity and risk of accidental clears.
- **Correction:** Add a PATCH endpoint for partial updates, or make PUT merge semantics (only update provided non-null fields). Keep PUT for full replacement if desired, but document clearly.
- **Regression test:** Test that PATCH with only `name` preserves `BreedId`, `AreaId`, etc.

#### F-005: `UpdateAnimal` validation allows `Quantity` change for `Unspecified` legacy records to 0 without classifying as Group
- **File:** `UpdateAnimal.cs` (lines 93–105), `FarmStatsDbContext.AnimalTracking.cs` (lines 37–43)
- **Behavior:** Validator at line 100–104 rejects `quantity < 1` for `Unspecified` with message "Classify the legacy record as a group before setting quantity to zero." However, the validator runs *before* `trackingType` is resolved (line 76: `var trackingType = req.TrackingType ?? animal.TrackingType;`). If request sends `trackingType: 2` (Group) and `quantity: 0`, it passes the validator but the DB check constraint `CK_Animals_TrackingType_Quantity` allows `TrackingType=2 AND Quantity>=0`. The issue: the *error message* implies classification is required first, but the code allows it in one request.
- **Failure scenario:** Legacy animal (`TrackingType=0, Quantity=5`). Request: `{ "revision": "...", "trackingType": 2, "quantity": 0 }` → succeeds, animal becomes Group with Quantity=0. The validation message said this shouldn't be allowed.
- **Why it matters:** Inconsistent validation messaging; legacy classification path differs from documented intent.
- **Correction:** Either (a) update validator to allow `quantity >= 0` when `trackingType == Group` is explicitly provided in the same request, or (b) enforce two-step classification (first classify, then update quantity). Option (a) is simpler and matches DB constraint.
- **Regression test:** `AnimalTrackingTests.LegacyRecordCanBeClassifiedOnce` should cover `quantity: 0` with explicit `trackingType: 2`.

#### F-006: `RecordAnimalQuantityEvent` does not validate `EffectiveDate` against future dates beyond UTC "today"
- **File:** `RecordAnimalQuantityEvent.cs` (lines 252–257), `RecordAnimalQuantityEvent.cs` (lines 65–77)
- **Behavior:** Validator ensures `EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow)`. Handler additionally checks `EffectiveDate >= Birthdate` and `>= StatusEffectiveDate`. But "today" is UTC date — a farm in UTC-12 could have a valid local date that is "tomorrow" UTC.
- **Failure scenario:** Farm in UTC+12 records event at 23:00 local (11:00 UTC previous day). `EffectiveDate` = local today. UTC "today" is yesterday → validator rejects valid entry.
- **Why it matters:** Timezone-naive date comparison breaks for non-UTC farms.
- **Correction:** Accept `EffectiveDate` up to `DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)` (allow one day forward) or store farm timezone and compare in farm-local time. Minimum: document the UTC assumption clearly.
- **Regression test:** Test with `EffectiveDate` = UTC tomorrow (should pass with +1 day allowance) and UTC tomorrow + 1 (should fail).

#### F-007: `GetAnimals` query lacks deterministic ordering for pagination stability
- **File:** `GetAnimals.cs` (lines 59–61)
- **Behavior:** `OrderBy(a => a.Name).ThenBy(a => a.Id)`. If two animals have the same name, `Id` tiebreaker ensures stability. However, `Name` is not unique — pagination can shift if names change between pages.
- **Failure scenario:** Page 1 returns animals A, B (names "Cow", "Cow"). User renames first "Cow" to "Aardvark". Page 2 now returns the other "Cow" (was page 1 item 2) — user sees duplicate/missing items.
- **Why it matters:** Pagination instability is a known UX issue; cursor-based pagination or `OrderBy(a => a.Id)` would be stable.
- **Correction:** Change default ordering to `OrderBy(a => a.Id)` (creation order, stable) or add `OrderBy(a => a.Name).ThenBy(a => a.Id)` with a note that name changes affect pagination. Or implement cursor-based pagination.
- **Regression test:** Integration test verifies that renaming an animal on page 1 does not cause it to appear on page 2.

#### F-008: `AnimalQuantityEventType.Correction` requires `Notes` but validator only checks `!string.IsNullOrWhiteSpace`
- **File:** `RecordAnimalQuantityEvent.cs` (lines 247–249), `FarmStatsDbContext.AnimalQuantityEvents.cs` (lines 41–46)
- **Behavior:** Validator: `RuleFor(req => req.Notes).Must(notes => !string.IsNullOrWhiteSpace(notes))`. DB constraint: `AND "Notes" IS NOT NULL AND length(btrim("Notes")) > 0`. These align. However, the validator's error message is generic; the DB constraint will also reject if application validation is bypassed.
- **Why it matters:** Duplicate validation (app + DB) is good defense-in-depth, but error messages differ (400 vs 409). The DB constraint returns 409 via the `DbUpdateException` middleware in `Program.cs` (line 57–62) which only handles `LookupUniqueness` conflicts — check constraint violations would bubble as 500.
- **Correction:** Extend `LookupUniqueness.IsConflict` or add a similar helper to detect check constraint violations (`SqlState 23514`) and return 400 with constraint name.
- **Regression test:** Direct SQL `INSERT` violating `CK_AnimalHistory_QuantityEvent` returns 400, not 500.

#### F-009: `ChangeAnimalStatus` allows transition to same status (validated) but no validation for invalid status transitions
- **File:** `ChangeAnimalStatus.cs` (lines 52–56)
- **Behavior:** Only checks `if (animal.Status == req.Status)` → 400. Allows any status → any other status (e.g., `Deceased` → `Active`, `Sold` → `Missing`).
- **Failure scenario:** Animal marked `Deceased` can be changed back to `Active` via status-change endpoint (not restore). History shows `Deceased` → `Active` which is semantically wrong.
- **Why it matters:** Lifecycle integrity; status transitions should follow a state machine.
- **Correction:** Define allowed transitions (e.g., `Active` → any terminal; terminal → none except via `RestoreAnimal` which doesn't change status). Reject invalid transitions at application level.
- **Regression test:** Test all invalid transitions return 400; history unchanged.

#### F-010: `AnimalHistoryEntry.EffectiveDate` nullable for non-quantity events but required for quantity events — schema allows inconsistency
- **File:** `AnimalHistoryEntry.cs` (line 23), `FarmStatsDbContext.AnimalQuantityEvents.cs` (line 30)
- **Behavior:** `EffectiveDate` is `DateOnly?` on entity. DB constraint requires it for `EventType=4` (QuantityChanged) but allows NULL for other event types. `Created`, `StatusChanged`, `Deleted`, `Restored` events can have NULL `EffectiveDate`.
- **Failure scenario:** `StatusChanged` event recorded without `EffectiveDate` — cannot reconstruct timeline accurately.
- **Why it matters:** Audit trail completeness; `StatusChanged` and `Created` should have effective dates.
- **Correction:** Make `EffectiveDate` required for `StatusChanged` and `Created` events (add to DB constraint). For `Created`, use `StatusEffectiveDate` or `Birthdate` as default.
- **Regression test:** Verify all history event types have non-null `EffectiveDate`.

---

### LOW

#### F-011: `AddAnimal` default `TrackingType = Individual` but `Quantity` default = 1 — inconsistent with explicit Group creation requiring `Quantity > 0`
- **File:** `AddAnimal.cs` (lines 129, 131), `AddAnimalValidator.cs` (lines 198–205)
- **Behavior:** Defaults create an Individual with Quantity=1. If client sends `TrackingType=Group` without `Quantity`, validator rejects (`Quantity > 0` required). But if client sends `Quantity=5` without `TrackingType`, validator rejects (Individual must have Quantity=1). This is correct but the error message "Initial quantity must be at least 1" doesn't explain the tracking-type dependency.
- **Correction:** Improve validator message to mention tracking type when quantity > 1 but type is Individual/default.

#### F-012: `Animal.Sex` default `Unknown` but `UpdateAnimal` validator requires explicit `Sex` (rejects default)
- **File:** `Animal.cs` (line 19), `UpdateAnimal.cs` (line 230), `UpdateAnimal.Validator.cs` (lines 312–315)
- **Behavior:** `Animal.Sex` defaults to `Unknown` (0). `UpdateAnimalRequest.Sex` defaults to `(AnimalSex)(-1)` to detect missing. Validator requires `IsInEnum()` — rejects `Unknown` if not sent? Actually `Unknown` is a valid enum value (0). The default `-1` forces client to send a value. But `AddAnimal` allows default `Unknown`. Inconsistent: create can omit sex, update cannot.
- **Correction:** Allow `Sex` to be optional in update (preserve existing) or align create to require it.

#### F-013: `AnimalHistoryEntry.ActorUserId` max length 450 — arbitrary, not tied to auth standard
- **File:** `AnimalHistoryEntry.cs` (line 33), `FarmStatsDbContext.cs` (line 149)
- **Behavior:** `HasMaxLength(450)` — likely from ASP.NET Core Identity default. No auth implemented.
- **Correction:** Use 256 (common JWT `sub` limit) or 450 with comment explaining origin.

#### F-014: `RecordAnimalQuantityEvent` response includes `RecordedAtUtc` but `AnimalHistoryItem` also has it — duplication
- **File:** `RecordAnimalQuantityEventResponse.cs` (line 200), `AnimalHistoryResponse.cs` (line 25)
- **Behavior:** Both return `RecordedAtUtc`. Minor API surface duplication.
- **Correction:** Remove from response if available via history endpoint; or keep for convenience.

#### F-015: `AnimalQuantityEventType` enum values (Addition=0, Death=1, Sale=2, Correction=3) — `Addition` as 0 risks default-value bugs
- **File:** `AnimalQuantityEventType.cs` (lines 4–8)
- **Behavior:** Default `AnimalQuantityEventType` is `Addition` (0). If client omits `eventType`, it defaults to Addition.
- **Correction:** Make `Addition = 1` and add `Unspecified = 0` with validator requiring valid value. Or keep as-is but ensure validator `IsInEnum()` catches default (it does: `eventType = (AnimalQuantityEventType)(-1)` in request DTO).

---

## D. Verification Results

| Check | Command | Result | Notes |
|-------|---------|--------|-------|
| API project build | `dotnet build src/FarmStats.API/FarmStats.API.csproj` | **PASS** | .NET 11 RC3, no warnings |
| Test project build | `dotnet build tests/FarmStats.API.IntegrationTests/...` | **FAIL** | Missing `Npgsql` package (F-001) |
| Test project build (with fix) | `dotnet add package Npgsql --version 9.0.3` + build | **NOT RUN** | Blocked by F-001; would likely pass |
| Integration tests | `dotnet test tests/...` | **NOT RUN** | Requires Docker + test project build |
| Migration/model consistency | `dotnet ef dbcontext script` | **NOT RUN** | Requires DB connection; migrations appear consistent with model snapshot |
| Code formatting | `dotnet format --verify-no-changes` | **NOT RUN** | No `.editorconfig` found; not enforced in CI |
| Dependency vulnerabilities | `dotnet list package --vulnerable` | **NOT RUN** | Preview .NET 11 packages; manual review needed |

**Environment limitations:** No Docker daemon available in this session; no PostgreSQL instance. Integration tests require Testcontainers (Docker). Could not run tests or verify migration application against real DB.

---

## E. Test and Documentation Gaps

| Gap | Description | Related Finding |
|-----|-------------|-----------------|
| Cross-entity concurrency test | No test for simultaneous animal-write + lookup-deletion race | F-002 |
| `ActorUserId` population test | No test verifies audit trail includes actor | F-003 |
| Partial update (PATCH) test | No PATCH endpoint exists; no test for partial update behavior | F-004 |
| Status transition state machine test | Invalid transitions (Deceased→Active) not tested/rejected | F-009 |
| Timezone-aware `EffectiveDate` test | No test for non-UTC farm date handling | F-006 |
| Pagination stability test | Renaming during pagination not tested | F-007 |
| Check constraint violation error handling | DB check constraint errors return 500, not 400 | F-008 |
| `EffectiveDate` required for all event types | `Created`/`StatusChanged` can have NULL `EffectiveDate` | F-010 |
| Authentication/authorization tests | All endpoints anonymous; no auth middleware tests | — |
| API versioning / OpenAPI contract tests | No contract tests against generated OpenAPI | — |
| Performance/load tests | No query plan analysis or load testing | — |

**Documentation gaps:**
- `docs/animal-tracking.md` referenced in historical context but **file not found** in repository
- `docs/ci.md` referenced in README but **file not found**
- No `AGENTS.md`, `CLAUDE.md`, or contributing guide
- README documents concurrency limitation but no mitigation timeline

---

## F. Recommended Next Steps (Ordered)

1. **Fix test project build** — Add `Npgsql` package to `FarmStats.API.IntegrationTests.csproj`. Enables all verification.
2. **Run integration tests** — With Docker available, execute full suite to confirm no regressions.
3. **Populate `ActorUserId`** — Add placeholder `"dev"` in all endpoint handlers; replace with real auth later. Closes F-003.
4. **Handle check constraint violations** — Extend `Program.cs` middleware to catch `PostgresException` with `SqlState 23514` and return 400 with constraint name. Closes F-008.
5. **Align `UpdateAnimal` quantity validation for legacy→group classification** — Allow `quantity >= 0` when `trackingType == Group` in same request. Update error message. Closes F-005.
6. **Add PATCH endpoint for partial animal updates** — Or change PUT to merge semantics. Closes F-004.
7. **Define and enforce status transition rules** — Add state machine validation in `ChangeAnimalStatus`. Closes F-009.
8. **Require `EffectiveDate` for `Created` and `StatusChanged` events** — Extend DB constraint and application validation. Closes F-010.
9. **Add cross-entity concurrency test** — Simulate animal-create + species-deletion race; verify consistent outcome or 409. Documents F-002 behavior.
10. **Implement authentication** — Replace `AllowAnonymous()` with policy; populate `ActorUserId` from claims. Unblocks F-003 fully.
11. **Fix pagination ordering** — Use `OrderBy(a => a.Id)` as stable default or add cursor pagination. Closes F-007.
12. **Document timezone assumption for `EffectiveDate`** — Or implement farm-local time support. Closes F-006.

---

## G. Open Questions

1. **Deployment model:** Is this intended for single-user local deployment only, or multi-user/farm? The answer determines priority of F-002 (cross-entity concurrency) and authentication.
2. **Timezone strategy:** Will farms operate in multiple timezones? If yes, `EffectiveDate` must be farm-local, not UTC.
3. **Group splits/mergers/transfers:** The historical context mentions these are "outside the initial quantity-event slice." Is there a design doc or issue tracking them? Current `AnimalQuantityEventType` doesn't cover them.
4. **`AnimalTrackingType.Unspecified` migration path:** Legacy records remain `Unspecified` forever unless explicitly classified. Is there a bulk-classification tool planned?
5. **Catalog seeding vs. user-created lookups:** Seeding skips deleted parents (correct). But if a user creates a breed under a species that is later deleted, the breed remains. Is that intended? (Current behavior: yes, breed is not cascade-deleted.)
6. **`ReferenceId` uniqueness:** Not enforced. Is duplicate `ReferenceId` (e.g., ear tag) allowed across animals?
7. **Soft-delete vs. status `Sold`/`Deceased`:** Both remove animal from active list. When should a user use status change vs. deletion? README says "Use a status change, not deletion, to record a sale or death" — but deletion also preserves quantity/history. Clarify in docs.

---

*End of review. No implementation changes were made.*