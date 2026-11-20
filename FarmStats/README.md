### Development setup

The API targets .NET 11 and PostgreSQL. All existing endpoints are anonymous and are intended for development only; do not expose this service to untrusted networks.

Configure the connection string with .NET User Secrets (replace the placeholders locally; do not commit credentials):

```sh
dotnet user-secrets set --project src/FarmStats.API 'ConnectionStrings:FarmStats' 'Host=localhost;Port=5433;Database=farmstats;Username=YOUR_USER;Password=YOUR_PASSWORD'
dotnet tool restore --tool-manifest dotnet-tools.json
ASPNETCORE_ENVIRONMENT=Development dotnet tool run dotnet-ef -- database update --project src/FarmStats.API --startup-project src/FarmStats.API
dotnet run --project src/FarmStats.API --launch-profile http
```

For the supplied Compose PostgreSQL service, set `POSTGRES_USER` and `POSTGRES_PASSWORD` in an untracked `.env`, then run `docker compose up -d postgres`. From the host use `localhost:5433`, as above. The Compose API container instead connects to `postgres:5432` on the Compose network; `compose.yaml` passes its connection string through environment variables. Do not run migration verification against this persistent development database.

### Domain and concurrency

Animal status changes and soft deletion are separate operations. Deleted animals and lookups are hidden from ordinary reads but remain available through their history and restoration workflows. Lookup deletion is blocked by retained references, including soft-deleted records, to preserve referential integrity; it never cascades. Deleted lookup names remain reserved. Seeding does not restore deleted catalog entries.

Each existing-record mutation requires the current `revision` in its request body. Reads and successful writes return a revision; use the newly returned value for the next mutation. An empty revision is invalid, and a stale revision produces HTTP 409. This protects edits to the same entity, not cross-entity races such as assigning a lookup concurrently with its deletion.

**Concurrent deployment limitation:** an animal create or update can validate an active species, breed or area; a concurrent deletion can then observe no retained references and soft-delete that lookup before the animal saves. Likewise, breed creation or reassignment can validate a species while its deletion sees no references, and breed restoration can validate its parent while that parent is being deleted. A breed reassignment can also check that no animal uses it while an animal concurrently validates and assigns it, leaving the animal's species and breed inconsistent. Foreign keys only check that rows exist, not whether they are soft-deleted or their relationships remain valid. Per-record revisions do not coordinate these different rows.

A complete follow-up must coordinate every relevant writer: animal creation and reassignment, breed creation, reassignment and restoration, and species/breed/area deletion, including any other writers such as catalog seeding. For example, transactions could acquire consistently ordered locks on affected lookup rows before validating and retaining them through the write, with deletion using the same protocol; alternatively, correctly scoped serializable transactions with retry of serialization failures could enforce the checks. The protocol must also cover the breed-move versus animal-assignment invariant and be tested under coordinated concurrency. Locking deletion alone is insufficient. This limitation is acceptable only for a development branch with controlled writes; the service is not ready for concurrent real-user deployment.

PUT replaces optional fields: an omitted or null optional field is cleared.

For example, after retrieving an animal's `revision`, send it with a status change:

```json
{ "revision": "6eb2d379-7c32-4e8f-a8df-f7edbb3b0f54", "status": 1, "effectiveDate": "2020-01-02" }
```

The successful response contains the new `revision`. Creation and reads do not require one. For a deleted record, use its history endpoint to retrieve the current revision before restoring it. Lifecycle conflicts and stale revisions return HTTP 409; invalid or missing revisions return HTTP 400.

### Lookup names and migration preflight

PostgreSQL computes `upper(btrim("Name"))` using the database's collation. `btrim` removes ordinary spaces at the ends, not all Unicode whitespace; PostgreSQL case mapping is collation-dependent and does not promise Unicode normalization or full case folding. API input is additionally trimmed with .NET `Trim()` before storage. Names are unique across all species and all areas, and within each species for breeds; deleted rows are included. Application duplicate checks return HTTP 400; races caught by the named unique indexes return HTTP 409.

Before applying the lookup uniqueness migration to existing data, run these read-only queries against the intended database. They include deleted rows:

```sql
SELECT upper(btrim("Name")) AS name, count(*) FROM "Species" GROUP BY 1 HAVING count(*) > 1;
SELECT "SpeciesId", upper(btrim("Name")) AS name, count(*) FROM "Breeds" GROUP BY 1, 2 HAVING count(*) > 1;
SELECT upper(btrim("Name")) AS name, count(*) FROM "Areas" GROUP BY 1 HAVING count(*) > 1;
```

If any rows are returned, inspect the matching records and their references and resolve the conflict manually before migration. The migration does not merge, delete, or rename records.

### Integration tests

The integration suite starts an isolated PostgreSQL container and never uses the configured development database. Docker must be running and able to obtain the `postgres:17-alpine` image.

```sh
dotnet restore tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj
dotnet test tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj
```

To repeat the duplicate-data upgrade check alone, run `dotnet test tests/FarmStats.API.IntegrationTests/FarmStats.API.IntegrationTests.csproj --filter FullyQualifiedName~DuplicateUpgradeTests`. It builds the historical schema using EF-generated SQL in a disposable container, checks the preflight and failed migration, resolves only its own fixture duplicates, and retries. The generated-SQL approach avoids running the current application's seeder against an older schema, where current-model columns do not yet exist.