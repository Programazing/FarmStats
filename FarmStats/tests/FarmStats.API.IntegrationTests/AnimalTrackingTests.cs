using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Animal.GetAnimal;
using FarmStats.API.Features.Animal.GetAnimals;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalTrackingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private WebApplicationFactory<AddAnimal> factory = null!;
    private HttpClient client = null!;
    private Guid speciesId;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();

        factory = new WebApplicationFactory<AddAnimal>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting(
                    "ConnectionStrings:FarmStats",
                    postgres.GetConnectionString());
            });

        client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>()
            .Database.MigrateAsync();

        var response = await client.PostAsJsonAsync(
            "/species",
            new { name = "Tracking test species" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        speciesId = (await response.Content
            .ReadFromJsonAsync<AddSpeciesResponse>())!.Id;
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();

        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    private async Task<GetAnimalResponse> Create(
        int trackingType,
        int quantity)
    {
        var response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Batch 1028",
            speciesId,
            trackingType,
            quantity,
            sex = 0
        });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var added = (await response.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        Assert.Equal(
            (AnimalTrackingType)trackingType,
            added.TrackingType);

        return (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{added.Id}"))!;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 150)]
    public async Task CreationAndReadsPreserveExplicitTrackingType(
        int type,
        int quantity)
    {
        var animal = await Create(type, quantity);

        Assert.Equal((AnimalTrackingType)type, animal.TrackingType);
        Assert.Equal(quantity, animal.Quantity);

        var page = (await client.GetFromJsonAsync<GetAnimalsResponse>(
            "/animals"))!;

        var item = Assert.Single(page.Items);

        Assert.Equal(animal.TrackingType, item.TrackingType);
        Assert.Equal(quantity, item.Quantity);
    }

    [Fact]
    public async Task OmittedCreationTypeDefaultsToIndividualWithoutInferringFromQuantity()
    {
        var response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Bessie",
            speciesId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var animal = (await response.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        Assert.Equal(AnimalTrackingType.Individual, animal.TrackingType);
        Assert.Equal(1, animal.Quantity);

        response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Not implicitly a group",
            speciesId,
            quantity = 150
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, -1)]
    public async Task InvalidCreationLeavesNoAnimalOrHistory(
        int trackingType,
        int quantity)
    {
        var response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Invalid",
            speciesId,
            trackingType,
            quantity
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        Assert.Equal(
            0,
            await db.Animals.IgnoreQueryFilters().CountAsync());

        Assert.Equal(0, await db.AnimalHistory.CountAsync());
    }

    [Fact]
    public async Task GroupCanReachZeroWithoutBecomingIndividualOrDeceased()
    {
        var animal = await Create(2, 150);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = animal.Revision,
                eventType = AnimalQuantityEventType.Sale,
                quantity = 150,
                effectiveDate = "2020-01-02"
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(AnimalTrackingType.Group, current.TrackingType);
        Assert.Equal(0, current.Quantity);
        Assert.Equal(animal.Status, current.Status);
        Assert.NotEqual(animal.Revision, current.Revision);

        response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = animal.Revision,
                eventType = AnimalQuantityEventType.Addition,
                quantity = 1,
                effectiveDate = "2020-01-02"
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = current.Revision,
                eventType = AnimalQuantityEventType.Addition,
                quantity = 1,
                effectiveDate = "2020-01-02"
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(AnimalTrackingType.Group, current.TrackingType);
        Assert.Equal(1, current.Quantity);
        Assert.Equal(animal.Status, current.Status);

        var history = (await client
            .GetFromJsonAsync<AnimalHistoryResponse>(
                $"/animal/{animal.Id}/history"))!;

        Assert.Equal(3, history.TotalCount);
        Assert.Equal(
            2,
            history.Items.Count(item =>
                item.EventType == AnimalHistoryEventType.QuantityChanged));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task ClassifiedRecordCannotSwitchType(
        int original,
        int requested)
    {
        var animal = await Create(original, 1);

        var before = await client.GetStringAsync(
            $"/animal/{animal.Id}");

        var history = await client.GetStringAsync(
            $"/animal/{animal.Id}/history");

        var response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = animal.Name,
                speciesId,
                trackingType = requested,
                quantity = 1,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(
            before,
            await client.GetStringAsync($"/animal/{animal.Id}"));

        Assert.Equal(
            history,
            await client.GetStringAsync($"/animal/{animal.Id}/history"));
    }

    [Fact]
    public async Task MissingQuantityIsNotTreatedAsAnEmptyGroup()
    {
        var animal = await Create(2, 10);

        var response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = animal.Name,
                speciesId,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(10, current.Quantity);
        Assert.Equal(animal.Revision, current.Revision);
    }

    [Fact]
    public async Task IndividualCannotChangeQuantityAndFailedWriteKeepsHistory()
    {
        var animal = await Create(1, 1);

        var history = await client.GetStringAsync(
            $"/animal/{animal.Id}/history");

        var response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = animal.Name,
                speciesId,
                quantity = 2,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(
            history,
            await client.GetStringAsync($"/animal/{animal.Id}/history"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 150)]
    public async Task LegacyRecordCanBeClassifiedOnce(
        int target,
        int quantity)
    {
        Guid id;
        Guid revision;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<FarmStatsDbContext>();

            var legacy = new Data.Entities.Animal
            {
                Name = "Reviewed legacy record",
                SpeciesId = speciesId,
                TrackingType = AnimalTrackingType.Unspecified,
                Quantity = quantity
            };

            db.Animals.Add(legacy);
            await db.SaveChangesAsync();

            id = legacy.Id;
            revision = legacy.Revision;
        }

        var response = await client.PutAsJsonAsync(
            $"/animal/{id}",
            new
            {
                revision,
                name = "Reviewed legacy record",
                speciesId,
                trackingType = target,
                quantity,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{id}"))!;

        Assert.Equal((AnimalTrackingType)target, current.TrackingType);
        Assert.Equal(quantity, current.Quantity);
        Assert.NotEqual(revision, current.Revision);

        response = await client.PutAsJsonAsync(
            $"/animal/{id}",
            new
            {
                revision = current.Revision,
                name = current.Name,
                speciesId,
                trackingType = 0,
                quantity,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletionAndRestorationPreserveGroupTypeAndQuantity()
    {
        var animal = await Create(2, 150);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/deletion",
            new
            {
                revision = animal.Revision,
                reason = AnimalDeletionReason.TestData
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = (await response.Content
            .ReadFromJsonAsync<AnimalLifecycleResponse>())!;

        Assert.Equal(AnimalTrackingType.Group, deleted.TrackingType);
        Assert.Equal(150, deleted.Quantity);

        response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/restore",
            new
            {
                revision = deleted.Revision
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var restored = (await response.Content
            .ReadFromJsonAsync<AnimalLifecycleResponse>())!;

        Assert.Equal(AnimalTrackingType.Group, restored.TrackingType);
        Assert.Equal(150, restored.Quantity);
    }

    [Fact]
    public async Task DatabaseCheckConstraintRejectsInvalidRawWrites()
    {
        var animal = await Create(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Animals\" SET \"Quantity\" = 2 WHERE \"Id\" = {animal.Id}"));

        Assert.Equal("23514", error.SqlState);
        Assert.Equal(
            "CK_Animals_TrackingType_Quantity",
            error.ConstraintName);
    }

    [Theory]
    [InlineData(0, 5, null, 15)]
    [InlineData(1, 3, null, 7)]
    [InlineData(2, 4, null, 6)]
    [InlineData(3, null, 12, 12)]
    [InlineData(3, null, 0, 0)]
    public async Task QuantityEventsUpdateCountRevisionAndHistory(
        int eventType,
        int? quantity,
        int? correctedQuantity,
        int expectedQuantity)
    {
        var animal = await Create(2, 10);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = animal.Revision,
                eventType,
                quantity,
                correctedQuantity,
                effectiveDate = "2020-01-02",
                notes = "  Verified count change.  "
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(expectedQuantity, current.Quantity);
        Assert.NotEqual(animal.Revision, current.Revision);
        Assert.Equal(animal.Status, current.Status);

        var history = (await client
            .GetFromJsonAsync<AnimalHistoryResponse>(
                $"/animal/{animal.Id}/history"))!;

        Assert.Equal(current.Revision, history.Revision);

        var entry = Assert.Single(
            history.Items,
            item => item.EventType
                == AnimalHistoryEventType.QuantityChanged);

        Assert.Equal(
            (AnimalQuantityEventType?)eventType,
            entry.QuantityEventType);

        Assert.Equal((int?)10, entry.PreviousQuantity);
        Assert.Equal((int?)expectedQuantity, entry.NewQuantity);
        Assert.Equal(
            (int?)(expectedQuantity - 10),
            entry.QuantityDelta);

        Assert.Equal(
            (DateOnly?)new DateOnly(2020, 1, 2),
            entry.EffectiveDate);

        Assert.Equal("Verified count change.", entry.Notes);
        Assert.Null(entry.ActorUserId);

        var created = Assert.Single(
            history.Items,
            item => item.EventType == AnimalHistoryEventType.Created);

        Assert.Null(created.QuantityEventType);
        Assert.Null(created.PreviousQuantity);
        Assert.Null(created.NewQuantity);
        Assert.Null(created.QuantityDelta);
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(0, 0, null, null)]
    [InlineData(0, -1, null, null)]
    [InlineData(0, 1, 12, null)]
    [InlineData(1, 11, null, null)]
    [InlineData(2, 11, null, null)]
    [InlineData(3, null, null, "Count correction")]
    [InlineData(3, null, -1, "Count correction")]
    [InlineData(3, 1, 12, "Count correction")]
    [InlineData(3, null, 12, null)]
    [InlineData(3, null, 12, "   ")]
    [InlineData(3, null, 10, "No change")]
    [InlineData(99, 1, null, null)]
    public async Task InvalidQuantityEventPreservesAnimalAndHistory(
        int eventType,
        int? quantity,
        int? correctedQuantity,
        string? notes)
    {
        var animal = await Create(2, 10);

        var before = await client.GetStringAsync(
            $"/animal/{animal.Id}");

        var historyBefore = await client.GetStringAsync(
            $"/animal/{animal.Id}/history");

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = animal.Revision,
                eventType,
                quantity,
                correctedQuantity,
                effectiveDate = "2020-01-02",
                notes
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(
            before,
            await client.GetStringAsync($"/animal/{animal.Id}"));

        Assert.Equal(
            historyBefore,
            await client.GetStringAsync($"/animal/{animal.Id}/history"));
    }

    [Fact]
    public async Task QuantityAdditionCannotOverflow()
    {
        var animal = await Create(2, int.MaxValue);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/quantity-events",
            new
            {
                revision = animal.Revision,
                eventType = AnimalQuantityEventType.Addition,
                quantity = 1,
                effectiveDate = "2020-01-02"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(int.MaxValue, current.Quantity);
        Assert.Equal(animal.Revision, current.Revision);

        var history = (await client
            .GetFromJsonAsync<AnimalHistoryResponse>(
                $"/animal/{animal.Id}/history"))!;

        Assert.Equal(1, history.TotalCount);
    }

    [Fact]
    public async Task GroupQuantityCannotChangeThroughOrdinaryUpdate()
    {
        var animal = await Create(2, 10);

        var historyBefore = await client.GetStringAsync(
            $"/animal/{animal.Id}/history");

        var response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = animal.Name,
                speciesId,
                quantity = 9,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal(10, current.Quantity);
        Assert.Equal(animal.Revision, current.Revision);

        Assert.Equal(
            historyBefore,
            await client.GetStringAsync($"/animal/{animal.Id}/history"));

        response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = current.Revision,
                name = "Renamed batch",
                speciesId,
                quantity = 10,
                sex = 0
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        current = (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;

        Assert.Equal("Renamed batch", current.Name);
        Assert.Equal(10, current.Quantity);
    }
}