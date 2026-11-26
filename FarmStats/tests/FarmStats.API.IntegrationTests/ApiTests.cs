using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Data.Seeding;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.GetAnimal;
using FarmStats.API.Features.Animal.GetAnimals;
using FarmStats.API.Features.Animal.Lifecycle;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Breed.AddBreed;
using FarmStats.API.Features.Area.AddArea;
using FarmStats.API.Features.Species.GetSpeciesHistory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class ApiTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();
    private WebApplicationFactory<AddAnimal> factory = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        factory = new WebApplicationFactory<AddAnimal>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:FarmStats", postgres.GetConnectionString());
        });
        client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private async Task<Guid> CreateSpecies(string name)
    {
        var response = await client.PostAsJsonAsync("/species", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AddSpeciesResponse>())!.Id;
    }

    private async Task<Guid> CreateAnimal(Guid speciesId, string name = "Daisy")
    {
        var response = await client.PostAsJsonAsync("/animal", new { name, speciesId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AddAnimalResponse>())!.Id;
    }

    private async Task<Guid> AnimalRevision(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        return await db.Animals.IgnoreQueryFilters().Where(a => a.Id == id).Select(a => a.Revision).SingleAsync();
    }

    private async Task<Guid> SpeciesRevision(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        return await db.Species.IgnoreQueryFilters().Where(s => s.Id == id).Select(s => s.Revision).SingleAsync();
    }

    [Fact]
    public async Task AnimalLifecyclePreservesHistoryAndFiltersDeletedAndInactive()
    {
        var species = await CreateSpecies("Integration species");
        var id = await CreateAnimal(species);
        var originalRevision = await AnimalRevision(id);
        var change = await client.PostAsJsonAsync($"/animal/{id}/status-changes", new
        {
            revision = originalRevision,
            status = AnimalStatus.Sold,
            effectiveDate = "2020-01-02",
            notes = " sold "
        });
        Assert.True(change.StatusCode == HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/animal/{id}/deletion", new { revision = originalRevision, reason = AnimalDeletionReason.TestData })).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<GetAnimalsResponse>("/animals"))!.TotalCount);
        Assert.Equal(1, (await client.GetFromJsonAsync<GetAnimalsResponse>("/animals?includeInactive=true"))!.TotalCount);

        var deleted = await client.PostAsJsonAsync($"/animal/{id}/deletion", new { revision = await AnimalRevision(id), reason = AnimalDeletionReason.TestData });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/animal/{id}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<GetAnimalsResponse>("/animals?includeInactive=true"))!.TotalCount);
        var history = await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{id}/history");
        Assert.Equal(3, history!.TotalCount);
        Assert.Contains(history.Items, item => item.EventType == AnimalHistoryEventType.StatusChanged && item.Notes == "sold");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/animal/{id}/restore", new { revision = await AnimalRevision(id), notes = "restored" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/animal/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/animal/{id}/restore", new { revision = await AnimalRevision(id) })).StatusCode);
        Assert.Equal(4, (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{id}/history"))!.TotalCount);
    }

    [Fact]
    public async Task InvalidRequestsDoNotAddHistory()
    {
        var species = await CreateSpecies("Validation species");
        var id = await CreateAnimal(species);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/animal", new { name = "Invalid", speciesId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/animal/{id}/status-changes", new { revision = await AnimalRevision(id), status = AnimalStatus.Active, effectiveDate = "2020-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/animal/{id}/deletion", new { revision = await AnimalRevision(id), reason = AnimalDeletionReason.Other })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/animals?page=0")).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{id}/history"))!.TotalCount);
    }

    [Fact]
    public async Task LookupDeletionRespectsReferencesIncludingSoftDeletedAnimals()
    {
        var species = await CreateSpecies("Referenced species");
        var id = await CreateAnimal(species);
        var deletion = await client.PostAsJsonAsync($"/animal/{id}/deletion", new { revision = await AnimalRevision(id), reason = AnimalDeletionReason.TestData });
        Assert.True(deletion.StatusCode == HttpStatusCode.OK, await deletion.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/species/{species}/deletion", new { revision = await SpeciesRevision(species), reason = 1 })).StatusCode);
    }

    [Fact]
    public async Task LookupNamesRemainReservedUntilRestored()
    {
        var species = await CreateSpecies("Unique species");
        var deletion = await client.PostAsJsonAsync($"/species/{species}/deletion", new { revision = await SpeciesRevision(species), reason = 1 });
        Assert.True(deletion.StatusCode == HttpStatusCode.OK, await deletion.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/species", new { name = " unique SPECIES " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/species/{species}/restore", new { revision = await SpeciesRevision(species) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/species/{species}/restore", new { revision = await SpeciesRevision(species) })).StatusCode);
    }

    private async Task<(Guid Id, Guid Revision)> CreateBreed(Guid species, string name = "Breed")
    {
        var response = await client.PostAsJsonAsync("/breed", new { name, speciesId = species });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AddBreedResponse>())!;
        return (result.Id, result.Revision);
    }

    private async Task<(Guid Id, Guid Revision)> CreateArea(string name = "Area")
    {
        var response = await client.PostAsJsonAsync("/area", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AddAreaResponse>())!;
        return (result.Id, result.Revision);
    }

    [Fact]
    public async Task CreationValidatesRequiredFieldsAndLookupRelationshipsWithoutPartialWrites()
    {
        var species = await CreateSpecies("Species A");
        var other = await CreateSpecies("Species B");
        var breed = await CreateBreed(other);
        var deletedSpecies = await CreateSpecies("Deleted species");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/species/{deletedSpecies}/deletion", new { revision = await SpeciesRevision(deletedSpecies), reason = 1 })).StatusCode);
        var deletedBreed = await CreateBreed(species, "Deleted breed");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/breed/{deletedBreed.Id}/deletion", new { revision = deletedBreed.Revision, reason = 1 })).StatusCode);
        var area = await CreateArea();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/area/{area.Id}/deletion", new { revision = area.Revision, reason = 1 })).StatusCode);

        object[] invalid = [
            new { speciesId = species }, new { name = "No species" },
            new { name = "Invalid enum", speciesId = species, sex = 999 },
            new { name = "Zero quantity", speciesId = species, quantity = 0 },
            new { name = "Missing species", speciesId = Guid.NewGuid() },
            new { name = "Deleted species", speciesId = deletedSpecies },
            new { name = "Missing breed", speciesId = species, breedId = Guid.NewGuid() },
            new { name = "Deleted breed", speciesId = species, breedId = deletedBreed.Id },
            new { name = "Wrong breed", speciesId = species, breedId = breed.Id },
            new { name = "Missing area", speciesId = species, areaId = Guid.NewGuid() },
            new { name = "Deleted area", speciesId = species, areaId = area.Id }
        ];
        foreach (var request in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/animal", request)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        Assert.Equal(0, await db.Animals.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task UpdateClearsOptionalFieldsAndRejectsInvalidOrStaleWrites()
    {
        var species = await CreateSpecies("Update species");
        var breed = await CreateBreed(species);
        var area = await CreateArea();

        var created = await client.PostAsJsonAsync("/animal", new
        {
            trackingType = 2,
            name = "Before",
            speciesId = species,
            breedId = breed.Id,
            areaId = area.Id,
            quantity = 2,
            sex = 0,
            referenceId = "ref",
            color = "brown",
            birthdate = "2020-01-01"
        });

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            await created.Content.ReadAsStringAsync());

        var animal = (await created.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        var update = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = "After",
                speciesId = species,
                quantity = animal.Quantity,
                sex = 0
            });

        Assert.True(
            update.StatusCode == HttpStatusCode.OK,
            await update.Content.ReadAsStringAsync());

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal("After", current.Name);
        Assert.Equal(animal.Quantity, current.Quantity);

        Assert.Null(current.BreedId);
        Assert.Null(current.AreaId);
        Assert.Null(current.ReferenceId);
        Assert.Null(current.Color);
        Assert.Null(current.Birthdate);

        Assert.NotEqual(animal.Revision, current.Revision);

        var stale = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = "Stale",
                speciesId = species,
                quantity = current.Quantity,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var invalid = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = current.Revision,
                name = "Invalid",
                speciesId = species,
                breedId = Guid.NewGuid(),
                quantity = current.Quantity,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var after = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(current.Name, after.Name);
        Assert.Equal(current.Quantity, after.Quantity);
        Assert.Equal(current.Revision, after.Revision);

        var history = (await client.GetFromJsonAsync<AnimalHistoryResponse>(
            $"/animal/{animal.Id}/history"))!;

        Assert.Equal(1, history.TotalCount);
    }

    [Fact]
    public async Task RetainedAnimalAndDeletedBreedBlockAllReferencedLookups()
    {
        var species = await CreateSpecies("Referenced");
        var breed = await CreateBreed(species);
        var area = await CreateArea();
        var response = await client.PostAsJsonAsync("/animal", new { name = "Animal", speciesId = species, breedId = breed.Id, areaId = area.Id });
        var animal = (await response.Content.ReadFromJsonAsync<AddAnimalResponse>())!;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var (kind, id, revision) in new[] { ("species", species, await SpeciesRevision(species)), ("breed", breed.Id, breed.Revision), ("area", area.Id, area.Revision) })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/{kind}/{id}/deletion", new { revision, reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/animal/{animal.Id}/deletion", new { revision = animal.Revision, reason = AnimalDeletionReason.TestData })).StatusCode);
        foreach (var (kind, id, revision) in new[] { ("species", species, await SpeciesRevision(species)), ("breed", breed.Id, breed.Revision), ("area", area.Id, area.Revision) })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/{kind}/{id}/deletion", new { revision, reason = 1 })).StatusCode);
        var other = await CreateSpecies("Breed parent");
        var deletedBreed = await CreateBreed(other, "Retained breed");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/breed/{deletedBreed.Id}/deletion", new { revision = deletedBreed.Revision, reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/species/{other}/deletion", new { revision = await SpeciesRevision(other), reason = 1 })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        Assert.Equal(0, await db.LookupHistory.CountAsync(h => h.LookupId == species || h.LookupId == breed.Id || h.LookupId == area.Id || h.LookupId == other));
    }

    [Fact]
    public async Task HistoryProvidesCurrentRevisionForRestoringDeletedRecords()
    {
        var species = await CreateSpecies("History species");
        var old = await SpeciesRevision(species);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/species/{species}/deletion", new { revision = old, reason = 1 })).StatusCode);
        var history = (await client.GetFromJsonAsync<HistoryResponse>($"/species/{species}/history"))!;
        Assert.NotEqual(old, history.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/species/{species}/restore", new { revision = old })).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<HistoryResponse>($"/species/{species}/history"))!.TotalCount);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/species/{species}/restore", new { revision = history.Revision })).StatusCode);
        var animal = await CreateAnimal(species);
        var revision = await AnimalRevision(animal);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/animal/{animal}/deletion", new { revision, reason = AnimalDeletionReason.TestData })).StatusCode);
        var animalHistory = (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{animal}/history"))!;
        Assert.NotEqual(revision, animalHistory.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/animal/{animal}/restore", new { revision })).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{animal}/history"))!.TotalCount);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/animal/{animal}/restore", new { revision = animalHistory.Revision })).StatusCode);
    }

    [Fact]
    public async Task DatabaseEnforcesNormalizedUniquenessForDeletedRows()
    {
        var species = await CreateSpecies("Unique DB species");
        var parent = await CreateSpecies("Breed uniqueness parent");
        var breed = await CreateBreed(parent, "Unique DB breed");
        var area = await CreateArea("Unique DB area");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/species/{species}/deletion", new { revision = await SpeciesRevision(species), reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/breed/{breed.Id}/deletion", new { revision = breed.Revision, reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/area/{area.Id}/deletion", new { revision = area.Revision, reason = 1 })).StatusCode);
        object[] duplicates = [new Species { Name = " unique db SPECIES " }, new Breed { Name = " unique db BREED ", SpeciesId = parent }, new FarmArea { Name = " unique db AREA " }];
        foreach (var duplicate in duplicates)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
            db.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<Npgsql.PostgresException>(error.InnerException);
            Assert.Equal("23505", ((Npgsql.PostgresException)error.InnerException!).SqlState);
        }
    }

    [Fact]
    public async Task SeedingIsRepeatableAndDoesNotRestoreDeletedCatalogEntries()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        await ReferenceDataSeeder.SeedAsync(db, true);
        var counts = (await db.Species.IgnoreQueryFilters().CountAsync(), await db.Breeds.IgnoreQueryFilters().CountAsync(), await db.Areas.IgnoreQueryFilters().CountAsync());
        var area = await db.Areas.FirstAsync();
        var breed = await db.Breeds.FirstAsync();
        var species = await db.Species.FirstAsync(s => s.Id == breed.SpeciesId);
        area.DeletedAtUtc = DateTimeOffset.UtcNow;
        breed.DeletedAtUtc = DateTimeOffset.UtcNow;
        species.DeletedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ReferenceDataSeeder.SeedAsync(db, true);
        Assert.Equal(counts, (await db.Species.IgnoreQueryFilters().CountAsync(), await db.Breeds.IgnoreQueryFilters().CountAsync(), await db.Areas.IgnoreQueryFilters().CountAsync()));
        Assert.NotNull((await db.Areas.IgnoreQueryFilters().SingleAsync(a => a.Id == area.Id)).DeletedAtUtc);
        Assert.NotNull((await db.Breeds.IgnoreQueryFilters().SingleAsync(b => b.Id == breed.Id)).DeletedAtUtc);
        Assert.NotNull((await db.Species.IgnoreQueryFilters().SingleAsync(s => s.Id == species.Id)).DeletedAtUtc);
    }
}