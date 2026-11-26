using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddSpecies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class ConcurrentMutationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly SaveBarrier barrier = new();

    private WebApplicationFactory<AddAnimal> factory = null!;
    private HttpClient client = null!;

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

                builder.ConfigureServices(services =>
                    services.AddDbContext<FarmStatsDbContext>(options =>
                        options.AddInterceptors(barrier)));
            });

        client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>()
            .Database.MigrateAsync();
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

    [Fact]
    public async Task SimultaneousStatusChangesWithSameRevisionCommitOnlyOnce()
    {
        var speciesResponse = await client.PostAsJsonAsync(
            "/species",
            new { name = "Race parent" });

        Assert.Equal(HttpStatusCode.Created, speciesResponse.StatusCode);

        var species = (await speciesResponse.Content
            .ReadFromJsonAsync<AddSpeciesResponse>())!;

        var animalResponse = await client.PostAsJsonAsync(
            "/animal",
            new
            {
                name = "Race animal",
                speciesId = species.Id
            });

        Assert.Equal(HttpStatusCode.Created, animalResponse.StatusCode);

        var animal = (await animalResponse.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/status-changes",
                new
                {
                    revision = animal.Revision,
                    status = AnimalStatus.Sold,
                    effectiveDate = "2020-01-02",
                    notes = "first"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/status-changes",
                new
                {
                    revision = animal.Revision,
                    status = AnimalStatus.Sold,
                    effectiveDate = "2020-01-02",
                    notes = "second"
                }));

        Assert.Equal(2, barrier.Arrivals);
        Assert.Equal(2, barrier.Contexts.Distinct().Count());

        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.OK);

        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var saved = await db.Animals.SingleAsync(
            item => item.Id == animal.Id);

        Assert.Equal(AnimalStatus.Sold, saved.Status);
        Assert.NotEqual(animal.Revision, saved.Revision);

        var history = await db.AnimalHistory
            .Where(item => item.AnimalId == animal.Id)
            .ToListAsync();

        Assert.Equal(2, history.Count);

        Assert.Single(
            history,
            item => item.EventType == AnimalHistoryEventType.StatusChanged);
    }

    [Fact]
    public async Task SimultaneousDuplicateSpeciesInsertsAreResolvedByUniqueIndex()
    {
        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                "/species",
                new { name = "Race species" }),
            client.PostAsJsonAsync(
                "/species",
                new { name = " race SPECIES " }));

        Assert.Equal(2, barrier.Arrivals);
        Assert.Equal(2, barrier.Contexts.Distinct().Count());

        var winner = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Created);

        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);

        var created = (await winner.Content
            .ReadFromJsonAsync<AddSpeciesResponse>())!;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var saved = await db.Species
            .IgnoreQueryFilters()
            .Where(item => item.Name.ToUpper() == "RACE SPECIES")
            .ToListAsync();

        Assert.Equal(created.Id, Assert.Single(saved).Id);
    }

    [Fact]
    public async Task SimultaneousQuantityEventsWithSameRevisionCommitOnlyOnce()
    {
        var speciesResponse = await client.PostAsJsonAsync(
            "/species",
            new { name = "Quantity race species" });

        Assert.Equal(HttpStatusCode.Created, speciesResponse.StatusCode);

        var species = (await speciesResponse.Content
            .ReadFromJsonAsync<AddSpeciesResponse>())!;

        var animalResponse = await client.PostAsJsonAsync(
            "/animal",
            new
            {
                name = "Quantity race batch",
                speciesId = species.Id,
                trackingType = AnimalTrackingType.Group,
                quantity = 10,
                sex = 0
            });

        Assert.Equal(HttpStatusCode.Created, animalResponse.StatusCode);

        var animal = (await animalResponse.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/quantity-events",
                new
                {
                    revision = animal.Revision,
                    eventType = AnimalQuantityEventType.Sale,
                    quantity = 3,
                    effectiveDate = "2020-01-02",
                    notes = "First request"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/quantity-events",
                new
                {
                    revision = animal.Revision,
                    eventType = AnimalQuantityEventType.Sale,
                    quantity = 3,
                    effectiveDate = "2020-01-02",
                    notes = "Second request"
                }));

        Assert.Equal(2, barrier.Arrivals);
        Assert.Equal(2, barrier.Contexts.Distinct().Count());

        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.OK);

        Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Conflict);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var saved = await db.Animals.SingleAsync(
            item => item.Id == animal.Id);

        Assert.Equal(7, saved.Quantity);
        Assert.NotEqual(animal.Revision, saved.Revision);

        var history = await db.AnimalHistory
            .Where(item => item.AnimalId == animal.Id)
            .ToListAsync();

        Assert.Equal(2, history.Count);

        var quantityEvent = Assert.Single(
            history,
            item => item.EventType == AnimalHistoryEventType.QuantityChanged);

        Assert.Equal((int?)10, quantityEvent.PreviousQuantity);
        Assert.Equal((int?)7, quantityEvent.NewQuantity);
        Assert.Equal((int?)(-3), quantityEvent.QuantityDelta);
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int armed;
        private int arrivals;

        public int Arrivals => Volatile.Read(ref arrivals);

        public ConcurrentBag<Guid> Contexts { get; } = new();

        public void Arm() => Volatile.Write(ref armed, 1);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 1)
            {
                Contexts.Add(eventData.Context!.ContextId.InstanceId);

                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    release.TrySetResult();
                }

                await release.Task.WaitAsync(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
            }

            return result;
        }
    }
}