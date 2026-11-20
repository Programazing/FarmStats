using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Animal.GetAnimals;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalHistoryRestoreTests : IAsyncLifetime
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

    [Fact]
    public async Task DeletedHistorySupportsFreshRestoreAndStableTiedOrdering()
    {
        var speciesResponse = await client.PostAsJsonAsync("/species", new { name = "History species" });
        Assert.Equal(HttpStatusCode.Created, speciesResponse.StatusCode);
        var species = (await speciesResponse.Content.ReadFromJsonAsync<AddSpeciesResponse>())!;
        var created = await client.PostAsJsonAsync("/animal", new { name = "Daisy", speciesId = species.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var animal = (await created.Content.ReadFromJsonAsync<AddAnimalResponse>())!;

        var deletion = await client.PostAsJsonAsync($"/animal/{animal.Id}/deletion", new { revision = animal.Revision, reason = AnimalDeletionReason.TestData });
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/animal/{animal.Id}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<GetAnimalsResponse>("/animals?includeInactive=true"))!.TotalCount);

        var deletedHistoryJson = await client.GetStringAsync($"/animal/{animal.Id}/history");
        var deletedHistory = (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{animal.Id}/history"))!;
        Assert.NotEqual(animal.Revision, deletedHistory.Revision);
        Assert.Equal(2, deletedHistory.TotalCount);
        Assert.Single(deletedHistory.Items, entry => entry.EventType == AnimalHistoryEventType.Deleted);
        Assert.DoesNotContain(deletedHistory.Items, entry => entry.EventType == AnimalHistoryEventType.Restored);

        var staleRestore = await client.PostAsJsonAsync($"/animal/{animal.Id}/restore", new { revision = animal.Revision, notes = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, staleRestore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/animal/{animal.Id}")).StatusCode);
        Assert.Equal(deletedHistoryJson, await client.GetStringAsync($"/animal/{animal.Id}/history"));

        var restore = await client.PostAsJsonAsync($"/animal/{animal.Id}/restore", new { revision = deletedHistory.Revision, notes = "restored" });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/animal/{animal.Id}")).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<GetAnimalsResponse>("/animals"))!.Items, item => item.Id == animal.Id);
        var restoredHistoryJson = await client.GetStringAsync($"/animal/{animal.Id}/history");
        var restoredHistory = (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{animal.Id}/history"))!;
        Assert.NotEqual(deletedHistory.Revision, restoredHistory.Revision);
        Assert.Equal(3, restoredHistory.TotalCount);
        Assert.Single(restoredHistory.Items, entry => entry.EventType == AnimalHistoryEventType.Deleted);
        Assert.Single(restoredHistory.Items, entry => entry.EventType == AnimalHistoryEventType.Restored && entry.Notes == "restored");

        var staleRetry = await client.PostAsJsonAsync($"/animal/{animal.Id}/restore", new { revision = deletedHistory.Revision });
        Assert.Equal(HttpStatusCode.Conflict, staleRetry.StatusCode);
        Assert.Equal(restoredHistoryJson, await client.GetStringAsync($"/animal/{animal.Id}/history"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/animal/{animal.Id}")).StatusCode);

        var timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
            Assert.Equal(3, await db.AnimalHistory.Where(entry => entry.AnimalId == animal.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.RecordedAtUtc, timestamp)));
        }

        var expectedIds = restoredHistory.Items.Select(entry => entry.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        var actualIds = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var history = (await client.GetFromJsonAsync<AnimalHistoryResponse>($"/animal/{animal.Id}/history?page={page}&pageSize=1"))!;
            Assert.Equal(3, history.TotalCount);
            var item = Assert.Single(history.Items);
            Assert.Equal(timestamp, item.RecordedAtUtc);
            actualIds.Add(item.Id);
        }
        Assert.Equal(expectedIds, actualIds);
    }
}