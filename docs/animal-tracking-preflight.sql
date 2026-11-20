-- Run against the current database BEFORE migration; read-only.
-- Review every existing record. Quantity alone does not establish its type.
SELECT "Id", "Name", "Quantity", "DeletedAtUtc"
FROM "Animals"
ORDER BY "Name", "Id";

-- These rows would prevent the new check constraint from being installed.
-- Resolve intentionally; the migration does not rewrite quantities.
SELECT "Id", "Name", "Quantity", "DeletedAtUtc"
FROM "Animals"
WHERE "Quantity" < 1;
