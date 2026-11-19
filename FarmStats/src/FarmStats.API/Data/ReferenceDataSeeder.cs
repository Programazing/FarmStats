using System.Text.Json;
using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Data.Seeding;

public static class ReferenceDataSeeder
{
    private const string ResourcePrefix = "FarmStats.Seeding.";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static void Seed(
        FarmStatsDbContext db,
        bool includeDevelopmentAreas = false)
    {
        var species = db.Species.IgnoreQueryFilters().ToList();
        var breeds = db.Breeds.IgnoreQueryFilters().ToList();

        var areas = includeDevelopmentAreas
            ? db.Areas.IgnoreQueryFilters().ToList()
            : new List<FarmArea>();

        ApplyCatalogs(
            db,
            species,
            breeds,
            areas,
            includeDevelopmentAreas);

        db.SaveChanges();
    }

    public static async Task SeedAsync(
        FarmStatsDbContext db,
        bool includeDevelopmentAreas = false,
        CancellationToken ct = default)
    {
        var species = await db.Species.IgnoreQueryFilters().ToListAsync(ct);
        var breeds = await db.Breeds.IgnoreQueryFilters().ToListAsync(ct);

        var areas = includeDevelopmentAreas
            ? await db.Areas.IgnoreQueryFilters().ToListAsync(ct)
            : new List<FarmArea>();

        ApplyCatalogs(
            db,
            species,
            breeds,
            areas,
            includeDevelopmentAreas);

        await db.SaveChangesAsync(ct);
    }

    private static void ApplyCatalogs(
        FarmStatsDbContext db,
        List<Species> existingSpecies,
        List<Breed> existingBreeds,
        List<FarmArea> existingAreas,
        bool includeDevelopmentAreas)
    {
        var speciesIdMap = new Dictionary<Guid, Guid>();

        foreach (var seed in ReadCatalog<NamedSeed>("species.json"))
        {
            var match = FindMatch(
                existingSpecies,
                seed.Id,
                seed.Name,
                [],
                entity => entity.Id,
                entity => entity.Name);

            if (match is null)
            {
                match = new Species
                {
                    Id = seed.Id,
                    Name = seed.Name
                };

                db.Species.Add(match);
                existingSpecies.Add(match);
            }

            speciesIdMap.Add(seed.Id, match.Id);
        }

        var assembly = typeof(ReferenceDataSeeder).Assembly;

        var breedResources = assembly
            .GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(
                    ResourcePrefix,
                    StringComparison.Ordinal)
                && name.EndsWith(
                    ".breeds.json",
                    StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (breedResources.Length == 0)
        {
            throw new InvalidOperationException(
                "No embedded breed catalogs were found. "
                + "Check the EmbeddedResource configuration.");
        }

        foreach (var resource in breedResources)
        {
            var filename = resource[ResourcePrefix.Length..];

            foreach (var seed in ReadCatalog<BreedSeed>(filename))
            {
                if (!speciesIdMap.TryGetValue(
                        seed.SpeciesId,
                        out var actualSpeciesId))
                {
                    throw new InvalidOperationException(
                        $"Breed '{seed.Name}' references an unknown "
                        + $"catalog species ID '{seed.SpeciesId}'.");
                }

                // Do not add catalog children underneath a deleted parent.
                // Existing deleted records remain deleted; seeding is not restoration.
                if (existingSpecies.Any(species =>
                        species.Id == actualSpeciesId
                        && species.DeletedAtUtc != null))
                {
                    continue;
                }

                var sameId = existingBreeds
                    .SingleOrDefault(entity => entity.Id == seed.Id);

                if (sameId is not null
                    && sameId.SpeciesId != actualSpeciesId)
                {
                    throw new InvalidOperationException(
                        $"Breed ID '{seed.Id}' already belongs "
                        + "to a different species.");
                }

                var speciesBreeds = existingBreeds
                    .Where(entity =>
                        entity.SpeciesId == actualSpeciesId);

                var match = FindMatch(
                    speciesBreeds,
                    seed.Id,
                    seed.Name,
                    seed.Aliases,
                    entity => entity.Id,
                    entity => entity.Name);

                if (match is not null)
                {
                    continue;
                }

                var breed = new Breed
                {
                    Id = seed.Id,
                    Name = seed.Name,
                    SpeciesId = actualSpeciesId
                };

                db.Breeds.Add(breed);
                existingBreeds.Add(breed);
            }
        }

        if (!includeDevelopmentAreas)
        {
            return;
        }

        foreach (var seed in ReadCatalog<NamedSeed>(
                     "development-areas.json"))
        {
            var match = FindMatch(
                existingAreas,
                seed.Id,
                seed.Name,
                [],
                entity => entity.Id,
                entity => entity.Name);

            if (match is not null)
            {
                continue;
            }

            var area = new FarmArea
            {
                Id = seed.Id,
                Name = seed.Name
            };

            db.Areas.Add(area);
            existingAreas.Add(area);
        }
    }

    private static T? FindMatch<T>(
        IEnumerable<T> existing,
        Guid seedId,
        string seedName,
        IEnumerable<string> aliases,
        Func<T, Guid> getId,
        Func<T, string> getName)
        where T : class
    {
        var acceptedNames = aliases
            .Append(seedName)
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var matches = existing
            .Where(entity =>
                getId(entity) == seedId
                || acceptedNames.Contains(
                    Normalize(getName(entity))))
            .ToList();

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple existing records match '{seedName}'. "
                + "Resolve the duplicates before seeding.");
        }

        var match = matches.SingleOrDefault();

        if (match is not null
            && getId(match) == seedId
            && !acceptedNames.Contains(Normalize(getName(match))))
        {
            throw new InvalidOperationException(
                $"Catalog ID '{seedId}' for '{seedName}' "
                + $"already identifies '{getName(match)}'.");
        }

        return match;
    }

    private static List<T> ReadCatalog<T>(string filename)
    {
        var assembly = typeof(ReferenceDataSeeder).Assembly;
        var resourceName = ResourcePrefix + filename;

        using var stream = assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded catalog '{resourceName}' was not found.");

        return JsonSerializer.Deserialize<List<T>>(
                   stream,
                   JsonOptions)
               ?? throw new InvalidOperationException(
                   $"Catalog '{filename}' contains no JSON array.");
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    public sealed class NamedSeed
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }
    }

    public sealed class BreedSeed
    {
        public required Guid Id { get; init; }

        public required Guid SpeciesId { get; init; }

        public required string Name { get; init; }

        public string[] Aliases { get; init; } = [];
    }
}