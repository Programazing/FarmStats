# FarmStats: dated animal notes

## Scope and endpoints

Adds dated notes to individual and group animal records.

- `POST /animal/{Id}/notes` — create a note
- `GET /animal/{Id}/notes` — list notes (paginated)

Notes are separate from the immutable `AnimalHistory` audit trail. They represent user observations and may be edited or removed in future slices.

## Entity

**AnimalNote**

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `AnimalId` | Guid | FK to Animals, restrictive delete |
| `Text` | string | Required, trimmed, max 2000 UTF-16 code units |
| `EffectiveDate` | DateOnly | Client-supplied business date |
| `RecordedAtUtc` | DateTimeOffset | Server-generated at persistence time |
| `ActorUserId` | string? | Reserved for future auth; null in this slice |

Database check constraint `CK_AnimalNotes_Text_NotEmpty` enforces `length(btrim("Text")) > 0`.

Index: `AnimalId, RecordedAtUtc DESC, Id ASC` for deterministic pagination.

Append-only guard at application level (EF `SaveChanges` interceptor); bypassable by direct SQL.

## Create note

`POST /animal/{Id}/notes`

### Request

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| `Revision` | Guid | Yes | Must match animal's current revision |
| `Id` | Guid (route) | Yes | Non-empty |
| `Text` | string | Yes | Non-empty after trim, ≤ 2000 chars |
| `EffectiveDate` | DateOnly | Yes | Not in future (UTC) |

No birthdate or lifecycle-status-date restriction.

### Response — 201 Created

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Animal ID |
| `Revision` | Guid | Animal's **new** revision |
| `NoteId` | Guid | Created note ID |
| `Text` | string | Stored text (trimmed) |
| `EffectiveDate` | DateOnly | Client-supplied |
| `RecordedAtUtc` | DateTimeOffset | Server timestamp |

### Status codes

| Code | Condition |
|------|-----------|
| 201 | Note created, animal revision bumped |
| 400 | Invalid text, effective date, or missing revision |
| 404 | Animal not found or soft-deleted |
| 409 | Revision mismatch (concurrent modification) |

No `Location` header — no single-note GET route exists in this slice.

## List notes

`GET /animal/{Id}/notes?page=1&pageSize=25`

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
| `Items` | `AnimalNoteItem[]` | Page of notes |
| `Page` | int | Current page |
| `PageSize` | int | Page size |
| `TotalCount` | int | Total notes for this animal |
| `TotalPages` | int | Computed |
| `HasPreviousPage` | bool | `Page > 1` |
| `HasNextPage` | bool | `Page < TotalPages` |

**AnimalNoteItem**: `Id`, `Text`, `EffectiveDate`, `RecordedAtUtc`, `ActorUserId`

### Ordering

`RecordedAtUtc DESC, Id ASC` — deterministic, matches `GET /animal/{Id}/history`.

### Visibility

| Animal state | List notes |
|--------------|------------|
| Active | ✅ 200 |
| Inactive (Sold, Deceased, etc.) | ✅ 200 |
| Soft-deleted | ✅ 200 (uses `IgnoreQueryFilters()`) |
| Non-existent | ❌ 404 |

## Concurrency

- Requires current animal `Revision` on creation.
- Performs revision-protected animal `UPDATE` + note `INSERT` in one `SaveChanges` transaction.
- Revision bumped via `PrepareLifecycleChanges()` (sets `Revision = Guid.NewGuid()` on `EntityState.Modified`).
- No `AnimalHistoryEntry` is manufactured.
- Animal `Quantity`, `Status`, `TrackingType` unchanged.
- Stale revision → 409.
- `DbUpdateConcurrencyException` → 409.

## Migration

Generated via EF tooling: `20261127073731_AddAnimalNotes`.

Creates `AnimalNotes` table with FK, index, check constraint.

Additive schema — compatible with online deployment but not guaranteed zero-downtime.

Downgrade drops `AnimalNotes` table (destroys notes in that database).

## Whitespace and length semantics

- API trims with .NET `string.Trim()` (removes all Unicode whitespace).
- Rejects empty-after-trim at validation layer.
- Database constraint uses `btrim("Text")` (PostgreSQL: removes spaces only, not all Unicode whitespace).
- Direct SQL inserts with tabs/newlines/non-breaking spaces pass the DB constraint but are rejected by API validation.
- Stored length limit: 2000 UTF-16 code units **after** API trimming.

## HTTP examples

See `Features/Animal/AddAnimalNote/AddAnimalNote.http` and `Features/Animal/GetAnimalNotes/GetAnimalNotes.http`.

⚠ Manual HTTP requests write to the configured API database. Use disposable fixtures.

## Related documentation

- [Animal tracking](animal-tracking.md)
- [Group quantity events](animal-quantity-events.md)
- [CI and verification](ci.md)