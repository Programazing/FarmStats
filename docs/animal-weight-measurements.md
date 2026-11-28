# FarmStats: dated animal weight measurements

## Scope and endpoints

Adds dated weight measurements to individual and group animal records.

- `POST /animal/{Id}/weight-measurements` — create a measurement
- `GET /animal/{Id}/weight-measurements` — list measurements (paginated)

Measurements are separate from the immutable `AnimalHistory` audit trail. They represent user observations and are append-only in this slice; editing/deletion deferred.

## Entity

**AnimalWeightMeasurement**

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `AnimalId` | Guid | FK to Animals, restrictive delete |
| `Value` | decimal | Required, positive, max 9999999.999, max 3 decimal places |
| `Unit` | WeightUnit | Required, explicit (Kilogram or Pound) |
| `MeasurementDate` | DateOnly | Client-supplied business date |
| `RecordedAtUtc` | DateTimeOffset | Server-generated at persistence time |
| `ActorUserId` | string? | Reserved for future auth; null in this slice |

Database check constraint `CK_AnimalWeightMeasurements_Value_Positive` enforces `"Value" > 0`.

Index: `AnimalId, MeasurementDate DESC, RecordedAtUtc DESC, Id ASC` for deterministic latest-weight selection and pagination.

Append-only guard at application level (EF `SaveChanges` interceptor); bypassable by direct SQL.

## WeightUnit enum

| Value | Name | Symbol |
|-------|------|--------|
| 0 | Kilogram | kg |
| 1 | Pound | lb |

## Create measurement

`POST /animal/{Id}/weight-measurements`

### Request

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| `Revision` | Guid | Yes | Must match animal's current revision |
| `Id` | Guid (route) | Yes | Non-empty |
| `Value` | decimal | Yes | > 0, ≤ 9999999.999, max 3 decimal places |
| `Unit` | WeightUnit | Yes | Must be 0 (Kilogram) or 1 (Pound) |
| `MeasurementDate` | DateOnly | Yes | Not in future (UTC) |

No birthdate or lifecycle-status-date restriction.

### Response — 201 Created

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Animal ID |
| `Revision` | Guid | Animal's **new** revision |
| `MeasurementId` | Guid | Created measurement ID |
| `Value` | decimal | Stored value |
| `Unit` | WeightUnit | Stored unit |
| `MeasurementDate` | DateOnly | Client-supplied |
| `RecordedAtUtc` | DateTimeOffset | Server timestamp |

### Status codes

| Code | Condition |
|------|-----------|
| 201 | Measurement created, animal revision bumped |
| 400 | Invalid value, unit, measurement date, or missing revision |
| 404 | Animal not found or soft-deleted |
| 409 | Revision mismatch (concurrent modification) |

No `Location` header — no single-measurement GET route exists in this slice.

## List measurements

`GET /animal/{Id}/weight-measurements?page=1&pageSize=25`

### Request

| Field | Type | Default | Validation |
|-------|------|---------|------------|
| `Id` | Guid (route) | — | Non-empty |
| `Page` | int | 1 | ≥ 1; `(page-1) * pageSize ≤ int.MaxValue` |
| `PageSize` | int | 25 | 1–100 |

### Response — 200 OK

| Field | Type | Description |
|-------|------|-------------|
| `Revision` | Guid | Animal's current revision (may become stale) |
| `Items` | `AnimalWeightMeasurementItem[]` | Page of measurements |
| `Page` | int | Current page |
| `PageSize` | int | Page size |
| `TotalCount` | int | Total measurements for this animal |
| `TotalPages` | int | Computed |
| `HasPreviousPage` | bool | `Page > 1` |
| `HasNextPage` | bool | `Page < TotalPages` |
| `LatestWeight` | `LatestWeightSummary?` | Latest measurement across all pages |

**AnimalWeightMeasurementItem**: `Id`, `Value`, `Unit`, `MeasurementDate`, `RecordedAtUtc`, `ActorUserId`

**LatestWeightSummary**: `MeasurementId`, `Value`, `Unit`, `MeasurementDate`, `RecordedAtUtc`

### Ordering

`MeasurementDate DESC, RecordedAtUtc DESC, Id ASC` — deterministic.

### Latest-weight selection

`LatestWeight` is computed over **all** measurements for the animal, independently of the requested page or page size. It follows the same ordering: most recent `MeasurementDate` wins; same-day ties broken by `RecordedAtUtc DESC`, then `Id ASC`. A backdated entry (earlier `MeasurementDate` recorded later) never displaces a later-dated measurement.

### Visibility

| Animal state | List measurements |
|--------------|-------------------|
| Active | ✅ 200 |
| Inactive (Sold, Deceased, etc.) | ✅ 200 |
| Soft-deleted | ✅ 200 (uses `IgnoreQueryFilters()`) |
| Non-existent | ❌ 404 |

## Latest weight in `GET /animal/{Id}`

The `GetAnimalResponse` includes a nullable `LatestWeightSummary? LatestWeight` field, computed the same way as the list endpoint.

## Concurrency

- Requires current animal `Revision` on creation.
- Performs revision-protected animal `UPDATE` + measurement `INSERT` in one `SaveChanges` transaction.
- Revision bumped via `PrepareLifecycleChanges()` (sets `Revision = Guid.NewGuid()` on `EntityState.Modified`).
- No `AnimalHistoryEntry` is manufactured.
- Animal `Quantity`, `Status`, `TrackingType` unchanged.
- Stale revision → 409.
- `DbUpdateConcurrencyException` → 409.

## Append-only guard

`FarmStatsDbContext.PrepareLifecycleChanges()` blocks `EntityState.Modified | EntityState.Deleted` for `AnimalWeightMeasurement`. This is an application-level guard only; direct SQL can bypass it. Documented for transparency — future correction policy remains undecided.

## Migration

Generated via EF tooling: `20261127142927_AddAnimalWeightMeasurements`.

Creates `AnimalWeightMeasurements` table with FK, index, check constraint.

Additive schema — compatible with online deployment but not guaranteed zero-downtime.

Downgrade drops `AnimalWeightMeasurements` table (destroys measurements in that database).

## Precision and range semantics

- API validates `Value > 0`, `Value ≤ 9999999.999`, and at most 3 fractional decimal digits **before** persistence.
- Database column: `numeric(10,3)` — supports up to 9,999,999.999.
- Trailing zeros in the serialized JSON (e.g., `1.2300`) are accepted and normalized to the stored precision (1.230).
- Values requiring more than 3 decimal places (e.g., `1.2345`) are rejected with 400.
- Unit is stored as supplied; no conversion or normalization at write time.

## HTTP examples

See `Features/Animal/AddAnimalWeightMeasurement/AddAnimalWeightMeasurement.http` and `Features/Animal/GetAnimalWeightMeasurements/GetAnimalWeightMeasurements.http`.

⚠ Manual HTTP requests write to the configured API database. Use disposable fixtures.

## Related documentation

- [Animal tracking](animal-tracking.md)
- [Group quantity events](animal-quantity-events.md)
- [Dated animal notes](animal-notes.md)
- [CI and verification](ci.md)