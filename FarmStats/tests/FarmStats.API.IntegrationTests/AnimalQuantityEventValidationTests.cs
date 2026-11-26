using System.Net;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddSpecies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalQuantityEventValidationTests : IAsyncLifetime
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
            new { name = "Quantity validation species" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

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

    private async Task<(Guid Id, Guid Revision)> Create(
        AnimalTrackingType trackingType = AnimalTrackingType.Group,
        AnimalStatus status = AnimalStatus.Active,
        bool deleted = false,
        DateOnly? birthdate = null,
        DateOnly? statusDate = null)
    {
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var animal = new Data.Entities.Animal
        {
            Name = "Quantity validation record",
            SpeciesId = speciesId,
            TrackingType = trackingType,
            Quantity = trackingType == AnimalTrackingType.Individual ? 1 : 10,
            Status = status,
            Birthdate = birthdate,
            StatusEffectiveDate = statusDate,
            DeletedAtUtc = deleted ? DateTimeOffset.UtcNow : null,
            DeletionReason = deleted ? AnimalDeletionReason.TestData : null
        };

        db.Animals.Add(animal);
        await db.SaveChangesAsync();

        return (animal.Id, animal.Revision);
    }

    private async Task AssertRejectedWithoutChanges(
        Guid id,
        object request,
        HttpStatusCode expectedStatus = HttpStatusCode.BadRequest)
    {
        // History remains readable for soft-deleted records.
        var before = await client.GetStringAsync(
            $"/animal/{id}/history");

        int quantity;
        Guid revision;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<FarmStatsDbContext>();

            var animal = await db.Animals
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == id);

            quantity = animal.Quantity;
            revision = animal.Revision;
        }

        var response = await client.PostAsJsonAsync(
            $"/animal/{id}/quantity-events",
            request);

        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {expectedStatus}, got {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        Assert.Equal(
            before,
            await client.GetStringAsync($"/animal/{id}/history"));

        using var verificationScope = factory.Services.CreateScope();

        var verificationDb = verificationScope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var saved = await verificationDb.Animals
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == id);

        Assert.Equal(quantity, saved.Quantity);
        Assert.Equal(revision, saved.Revision);
    }

    [Theory]
    [InlineData(AnimalTrackingType.Individual)]
    [InlineData(AnimalTrackingType.Unspecified)]
    public async Task NonGroupRecordsRejectQuantityEvents(
        AnimalTrackingType trackingType)
    {
        var animal = await Create(trackingType: trackingType);

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Theory]
    [InlineData(AnimalStatus.Sold)]
    [InlineData(AnimalStatus.Transferred)]
    [InlineData(AnimalStatus.Deceased)]
    [InlineData(AnimalStatus.Slaughtered)]
    [InlineData(AnimalStatus.Missing)]
    public async Task InactiveGroupsRejectQuantityEvents(
        AnimalStatus status)
    {
        var animal = await Create(status: status);

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Fact]
    public async Task DeletedGroupRejectsQuantityEvents()
    {
        var animal = await Create(deleted: true);

        await AssertRejectedWithoutChanges(
            animal.Id,
            new
            {
                revision = animal.Revision,
                eventType = AnimalQuantityEventType.Addition,
                quantity = 1,
                effectiveDate = "2020-01-02"
            },
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MissingAnimalReturnsNotFoundWithoutHistory()
    {
        var id = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(
            $"/animal/{id}/quantity-events",
            new
            {
                revision = Guid.NewGuid(),
                eventType = AnimalQuantityEventType.Addition,
                quantity = 1,
                effectiveDate = "2020-01-02"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        Assert.False(await db.AnimalHistory.AnyAsync(
            item => item.AnimalId == id));
    }

    [Fact]
    public async Task MissingEffectiveDateIsRejected()
    {
        var animal = await Create();

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1
        });
    }

    [Fact]
    public async Task FutureEffectiveDateIsRejected()
    {
        var animal = await Create();

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = DateOnly
                .FromDateTime(DateTime.UtcNow)
                .AddDays(1)
        });
    }

    [Fact]
    public async Task EffectiveDateBeforeBirthdateIsRejected()
    {
        var animal = await Create(
            birthdate: new DateOnly(2020, 1, 3));

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Fact]
    public async Task EffectiveDateBeforeCurrentStatusDateIsRejected()
    {
        var animal = await Create(
            statusDate: new DateOnly(2020, 1, 3));

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Fact]
    public async Task MissingRevisionIsRejected()
    {
        var animal = await Create();

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Fact]
    public async Task MissingEventTypeIsRejected()
    {
        var animal = await Create();

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            quantity = 1,
            effectiveDate = "2020-01-02"
        });
    }

    [Fact]
    public async Task ExcessivelyLongNotesAreRejected()
    {
        var animal = await Create();

        await AssertRejectedWithoutChanges(animal.Id, new
        {
            revision = animal.Revision,
            eventType = AnimalQuantityEventType.Addition,
            quantity = 1,
            effectiveDate = "2020-01-02",
            notes = new string('x', 2001)
        });
    }
}