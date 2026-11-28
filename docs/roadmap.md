# FarmStats roadmap

## Purpose and status

This document preserves the animal-refinement discussion and separates completed foundations from proposed work. It is not authorization to implement every item.

Initial baseline: conversation reports through November 27, 2026. Completion statements below reflect that conversation, not an independent inspection of the current checkout or hosted services. Verify relevant implementation before relying on a capability.

## Established product decisions

- One animal record can represent an individual or a group, including a named batch such as `Batch 1028`.
- Tracking type is explicit, not inferred from name or quantity.
- An individual has quantity one; a group remains a group at one or zero.
- Zero quantity does not automatically change lifecycle status.
- Partial group sales/deaths differ from whole-record lifecycle changes.
- Acquisition, reproductive condition, location, archiving, and soft deletion are distinct concerns.
- Growing record sets such as measurements, notes, health, and breeding should not turn the basic animal update into an endpoint that edits everything.

## Completed foundations — reported integrated

| Foundation | Conversation baseline |
| --- | --- |
| Basic animal identity and lookup relationships | Implemented before the refinement work |
| Lifecycle, reasoned soft deletion, restoration, and history | Implemented; preserve these rather than rebuilding them |
| Revision-based concurrency and normalized lookup-name uniqueness | Implemented with integration and migration coverage; cross-entity limitations remain separate |
| Explicit `Unspecified`, `Individual`, and `Group` tracking | Reported merged into `dev`; legacy records are not automatically classified |
| Reasoned group quantity events | Addition, Death, Sale, and Correction reported merged in PR #3; state and history saved together |
| Quantity-event documentation and manual HTTP workflow | Reported merged; presence of a workflow is not proof it was executed |
| GitHub/Gitea CI configuration | Reported added; current hosted status must be checked separately |
| Temporary review-notes removal | Reported merged in PR #4; not a continuing product task |

The latest conversation report stated that `dev` was at `67eb2a1`, the working tree was clean, the build succeeded, and 104 integration tests passed with none failed or skipped. These are historical results, not a current verification requirement or a fixed expected test count. Do not infer that development-database migrations or manual HTTP workflows were executed from these results.

Relevant maintained documents:

- [Development setup](../FarmStats/README.md)
- [Animal tracking](animal-tracking.md)
- [Group quantity events](animal-quantity-events.md)
- [Dated animal notes](animal-notes.md)
- [Dated animal weight measurements](animal-weight-measurements.md)
- [CI and verification](ci.md)

## Proposed implementation order — not yet approved

This ordering was suggested after recovering the earlier discussion. It must not be treated as a previously approved backlog. Confirm the selected task and resolve its design questions before implementation.

### 1. Shared group-field semantics and mixed sex — DEFERRED

Deferred until explicitly requested. See [Deferred group operations](#deferred-group-operations---require-dedicated-designs).

### 2. Dated animal notes — MERGED

Original direction: individual dated entries, separate from the basic animal update.

Design questions resolved for first slice:
- Effective date (client-supplied) versus recorded timestamp (server-generated).
- Notes are append-only in this slice; editing/deletion deferred.
- Creation allowed for active and inactive nondeleted records; rejected for deleted (404).
- Listing available for deleted records via `IgnoreQueryFilters()`.

Acceptance criteria for first slice:
- Notes attach to individual or group records.
- Listing is paginated and deterministically ordered (RecordedAtUtc DESC, Id ASC).
- Validation and visibility rules documented and tested.
- Adding notes does not change quantity, lifecycle, tracking type, or other animal fields except Revision.
- Existing lifecycle and quantity-event notes remain attached to their own events.
- User notes are a separate entity from the immutable audit history.

Do not assume user notes must have exactly the same immutability contract as audit history.

### 3. Dated weight measurements — IN PROGRESS

Original direction: measurement history, not one overwritable weight field.

Design gate:

- Define individual measurements and supported units/precision.
- Decide whether the first version supports groups.
- If groups are supported, distinguish sample-average weight from total group weight; never store an ambiguous value.
- Define correction/removal behavior and the meaning of latest measurement.

Proposed first slice: dated value, explicit unit, optional notes, deterministic history, validation, and tests. Growth analytics and charting are separate follow-ups.

### 4. Acquisition information

Original direction: born on farm, purchased, gifted, or transferred in, with dated information and optional source/notes.

Resolve:

- Initial acquisition versus later additions to an existing group.
- Relationship to creation and quantity events so arrivals are not counted twice.
- Unknown legacy acquisition details without inventing dates or provenance.
- Scope of source information; do not introduce a full supplier/accounting subsystem implicitly.

### 5. Reproductive condition

Original direction: intact, sterilized, or unknown, separate from sex and lifecycle.

Resolve:

- Current field versus dated events/history.
- Applicability and mixed/unknown semantics for groups.
- Relationship to species and existing sex values.

Do not combine purchased, sterilized, sold, and deceased into one status enum.

## Further feature directions — ordering undecided

| Feature | Original direction / decision needed |
| --- | --- |
| Photos | Attachments with optional primary image; settle storage, access, limits, and removal |
| Lineage | Parent relationships and associated birth information; settle individual/group applicability and unknown parents |
| Health | Separate records; choose a specific initial use case before designing a subsystem |
| Breeding | Separate dated records and relationships to parents/offspring; define first workflow |
| Feeding | Dated records only if wanted; decide whether they attach to animals, groups, areas, or several scopes |
| Archiving | Hide valid historical records from everyday views without treating them as mistakes/deletions; reconcile with current inactive-list behavior |
| Location | Current area separate from ownership/life state; decide whether movement history or off-farm placement is required |
| Departure/death refinements | Review existing lifecycle events before adding detail; do not duplicate existing sale/death functionality |

## Deferred group operations — require dedicated designs

- Split a group into separately tracked records.
- Merge groups.
- Transfer members between groups or locations represented by separate records.
- Extract an individually tracked animal from a group while reducing the original count and preserving linkage.

Any future design must define quantity conservation, linked history, revisions across affected records, atomicity, compatibility rules, and behavior when records have different characteristics. These are not implicit extensions of ordinary PUT or the current quantity-event endpoint.

## Deployment-safety follow-ups — separate from feature sequencing

These are previously documented limitations or requirements to verify, not claims about a fresh review:

- Cross-entity lookup assignment/deletion and breed-reassignment races: same-record Revision tokens do not coordinate all related writers. Inspect current code to determine whether the limitations still apply. A fix must cover all relevant assignment and deletion/reassignment paths and include deterministic concurrency tests.
- Authentication and authorization: anonymous endpoints were explicitly development-only. Define server-derived actor identity; never accept a client-supplied identity as audit authority.
- Farm/tenant scoping: define ownership and isolation before multi-farm use. Review history/restore and other query-filter bypasses so they do not bypass farm scope.
- Confirm current branch protection, hosted checks, and deployment prerequisites when preparing a release. Do not assume workflow files prove that checks ran.

These follow-ups should gate a concurrent real-user deployment as applicable; they do not automatically authorize a broad security or tenancy rewrite now.

## Working agreement

Before starting any item:

1. User selects and approves the scope.
2. Inspect only the relevant implementation and maintained documentation.
3. Resolve the item's open contract/domain questions.
4. Define acceptance criteria, non-goals, migration impact, and validation.
5. Implement one bounded slice on an explicitly authorized branch.
6. Report actual results and remaining limitations; commit/push only when authorized.
7. Update this roadmap when status changes, linking the actual PR or decision when verified.

No active next feature is approved by this document alone. The current proposal is to start with shared group-field semantics and mixed sex.
