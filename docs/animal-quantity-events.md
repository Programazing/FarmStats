# FarmStats: group quantity events

## Scope and endpoint

Records a quantity change for an explicitly Group-tracked animal via the
append-only AnimalHistory trail. The endpoint is:

    POST /animal/{Id}/quantity-events

Requires an active, explicitly Group-tracked animal (TrackingType = 2) and its
current revision. The request body supplies the event type, quantity or corrected
quantity, effective date, and optional notes.

## Event enum values and request fields

**AnimalQuantityEventType** (namespace `FarmStats.API.Data.Entities`):

| Value | Name | Numeric |
|-------|------|---------|
| Addition | 0 |
| Death | 1 |
| Sale | 2 |
| Correction | 3 |

**RecordAnimalQuantityEventRequest fields:**

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Id | Guid (route) | Yes | Animal ID |
| Revision | Guid | Yes | Current revision for concurrency |
| EventType | AnimalQuantityEventType | Yes | Must be valid enum value |
| Quantity | int? | Conditional | Required for Addition, Death, Sale; must be > 0; null for Correction |
| CorrectedQuantity | int? | Conditional | Required for Correction; must be ≥ 0; null for Addition, Death, Sale |
| EffectiveDate | DateOnly | Yes | Cannot be future (UTC); cannot be before birthdate; cannot be before status effective date |
| Notes | string? | Conditional | Required (non-empty) for Correction; max 2000 chars |

## Addition, Death, Sale versus Correction semantics

- **Addition** (0): Increases the current count. `Quantity` must be positive.
  `QuantityDelta = +Quantity`.

- **Death** (1): Decreases the current count. `Quantity` must be positive.
  `QuantityDelta = -Quantity`.

- **Sale** (2): Decreases the current count. `Quantity` must be positive.
  `QuantityDelta = -Quantity`.

- **Correction** (3): Sets an absolute count via `CorrectedQuantity` (must be ≥ 0).
  The corrected quantity must differ from the current quantity (a correction must
  change the count). Requires explanatory `Notes` (non-empty after trimming).
  `QuantityDelta = CorrectedQuantity - PreviousQuantity` (may be positive or negative).

Addition, Death, and Sale use a **relative** `Quantity` (delta). Correction uses an
**absolute** `CorrectedQuantity` (new total).

All four event types (Addition, Death, Sale, Correction) are implemented.

## Validation rules

1. **Revision concurrency**: The supplied `Revision` must match the animal's
   current revision. Stale revision returns 409.

2. **Active Group requirement**: The animal must exist, not be deleted, have
   `Status = Active`, and `TrackingType = Group`. Violations return 404 (missing/deleted)
   or 400 (wrong state/type).

3. **Effective date**:
   - Cannot be after `DateOnly.FromDateTime(DateTime.UtcNow)` (today in UTC).
   - Cannot be before the animal's `Birthdate` (if set).
   - Cannot be before the animal's `StatusEffectiveDate` (if set).

4. **Non-negative result**: The computed new quantity cannot be < 0. Returns 400.

5. **Supported maximum**: The computed new quantity cannot exceed `int.MaxValue`. Returns 400.

6. **Correction-specific**:
   - `CorrectedQuantity` must be supplied and ≥ 0.
   - `Quantity` must be null.
   - `CorrectedQuantity` must differ from current quantity.
   - `Notes` must be non-empty after trimming.

7. **Note length**: `Notes` maximum 2000 characters.

## Response fields

**RecordAnimalQuantityEventResponse (200 OK):**

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Animal ID |
| Revision | Guid | New revision after save |
| EventId | Guid | The new AnimalHistoryEntry ID |
| EventType | AnimalQuantityEventType | The event type recorded |
| PreviousQuantity | int | Quantity before this event |
| Quantity | int | Current quantity after this event |
| QuantityDelta | int | Signed change (New - Previous) |
| EffectiveDate | DateOnly | The effective date supplied in the request |
| RecordedAtUtc | DateTimeOffset | Server timestamp when the event was recorded (UTC now) |

## Status codes

| Code | Condition |
|------|-----------|
| 200 | Success — event recorded, quantity updated, history appended |
| 400 | Validation failure: invalid event type, missing/incorrect quantity fields, effective date rules, negative result, correction without notes or no change, note length exceeded |
| 404 | Animal not found or deleted |
| 409 | Revision mismatch (concurrent modification) — reload and retry |

## Atomic quantity update and append-only AnimalHistory entry

The quantity update and the history insertion are executed in a single
`SaveChangesAsync` call. The endpoint:

1. Reads the animal with the supplied revision (concurrency token).
2. Computes the new quantity based on event type and request values.
3. Creates an `AnimalHistoryEntry` with:
   - `EventType = AnimalHistoryEventType.QuantityChanged` (value 4)
   - `QuantityEventType` = the request's event type
   - `PreviousQuantity`, `NewQuantity`, `QuantityDelta`
   - `EffectiveDate` = request's effective date
   - `RecordedAtUtc` = `DateTimeOffset.UtcNow`
   - `Notes` = cleaned request notes (trimmed, null if empty)
4. Sets the animal's `Quantity` to the new value and marks `Revision` as modified.
5. Saves both changes atomically. On `DbUpdateConcurrencyException`, returns 409.

The database check constraint `CK_AnimalHistory_QuantityEvent` (added by migration
`20261120060459_AddAnimalQuantityEvents`) enforces at the persistence layer:
- For non-quantity events: all quantity fields are NULL.
- For quantity events: all quantity fields are NOT NULL; delta equals new minus previous; previous/new ≥ 0; effective date not NULL; delta sign matches event type (Addition > 0; Death/Sale < 0; Correction ≠ 0 with non-empty notes).

## Difference between effectiveDate and recordedAtUtc

- **EffectiveDate** (DateOnly, client-supplied): The business date the event
  occurred. Used for backdating corrections or recording past sales/deaths.
  Must not be in the future (UTC). Not a timestamp — no time-of-day component.

- **RecordedAtUtc** (DateTimeOffset, server-generated): The exact moment the
  API processed and persisted the event. Always `DateTimeOffset.UtcNow` at save
  time. Immutable once written.

## Backdated events update the current count without replaying later events

If `EffectiveDate` is earlier than today, the quantity change is applied to the
**current** count immediately. The history entry records the effective date for
audit purposes, but subsequent events are **not** replayed or recalculated. The
trail shows what happened and when it was effective, but the running balance is
always the current `Animal.Quantity` value.

## Zero count does not automatically change lifecycle status

A Group's quantity may reach 0 through Death, Sale, or Correction. This does
**not** change the animal's `Status` (remains Active), does not trigger a
deletion, and does not convert the Group to an Individual. The Group retains its
identity and TrackingType. Lifecycle changes (deletion, archiving) require
explicit lifecycle endpoints.

## Existing history is preserved, not retroactively reconstructed

Backdated events insert a new history entry with the supplied `EffectiveDate`.
They do not rewrite prior history entries, recompute past balances, or insert
missing events. The AnimalHistory trail is append-only. Consumers that need a
point-in-time balance must read the trail and compute it.

## Unsupported scope

The following are explicitly **not** implemented in this feature:

- Transfers of animals between groups
- Splitting a group into multiple groups
- Merging groups
- Extracting an individual animal from a group
- Mixed sex value for groups
- Automatic lifecycle transitions on zero quantity

These would require separate design and endpoints.

## Workflow file

A Rider/REST Client workflow for manual testing is available at:

    FarmStats/src/FarmStats.API/Features/Animal/RecordAnimalQuantityEvent/RecordAnimalQuantityEvent.http

## ⚠ Manual HTTP requests use the configured API database

The HTTP workflow files target the running API instance (default
`http://127.0.0.1:5000` via the `@host` variable). They operate against **whatever
database the API is currently configured to use** — typically your development
database. They do **not** use the isolated PostgreSQL containers that integration
tests spin up. Run manual workflows only against a disposable or backed-up
database.

## Verification status

The user previously reported that all 104 integration tests passed in one
complete run, EF reported no pending model changes, and both `git diff --check`
and `git diff --cached --check` were clean. These are prior results, not
independently verified here. No development migration or manual HTTP run has
been completed as part of this documentation update.