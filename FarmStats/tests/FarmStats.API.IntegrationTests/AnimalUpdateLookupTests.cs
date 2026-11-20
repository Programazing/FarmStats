using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Area.AddArea;
using FarmStats.API.Features.Breed.AddBreed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalUpdateLookupTests : IAsyncLifetime
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

    private async Task<(Guid Id, Guid Revision)> Species(string name)
    {
        var response = await client.PostAsJsonAsync("/species", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AddSpeciesResponse>())!;
        return (result.Id, result.Revision);
    }

    private async Task<(Guid Id, Guid Revision)> Breed(Guid speciesId, string name)
    {
        var response = await client.PostAsJsonAsync("/breed", new { name, speciesId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AddBreedResponse>())!;
        return (result.Id, result.Revision);
    }

    private async Task<(Guid Id, Guid Revision)> Area(string name)
    {
        var response = await client.PostAsJsonAsync("/area", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AddAreaResponse>())!;
        return (result.Id, result.Revision);
    }

    [Theory]
    [InlineData("missing species", "SpeciesId")]
    [InlineData("deleted species", "SpeciesId")]
    [InlineData("missing breed", "BreedId")]
    [InlineData("deleted breed", "BreedId")]
    [InlineData("wrong species breed", "BreedId")]
    [InlineData("missing area", "AreaId")]
    [InlineData("deleted area", "AreaId")]
    public async Task InvalidLookupsLeaveAnimalRevisionAndHistoryUnchanged(string scenario, string field)
    {
        var species = await Species("Original species");
        var other = await Species("Other species");
        var breed = await Breed(species.Id, "Original breed");
        var area = await Area("Original area");
        var deletedSpecies = await Species("Deleted species");
        var deletedBreed = await Breed(species.Id, "Deleted breed");
        var wrongBreed = await Breed(other.Id, "Other breed");
        var deletedArea = await Area("Deleted area");
        foreach (var (kind, id, revision) in new[] { ("species", deletedSpecies.Id, deletedSpecies.Revision), ("breed", deletedBreed.Id, deletedBreed.Revision), ("area", deletedArea.Id, deletedArea.Revision) })
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/{kind}/{id}/deletion", new { revision, reason = 1 })).StatusCode);

        var created = await client.PostAsJsonAsync("/animal", new { name = "Before", speciesId = species.Id, breedId = breed.Id, areaId = area.Id, quantity = 2, sex = 0, referenceId = "ref", color = "brown", birthdate = "2020-01-01" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var animal = (await created.Content.ReadFromJsonAsync<AddAnimalResponse>())!;
        var before = await client.GetStringAsync($"/animal/{animal.Id}");
        var history = await client.GetStringAsync($"/animal/{animal.Id}/history");

        var speciesId = scenario switch { "missing species" => Guid.NewGuid(), "deleted species" => deletedSpecies.Id, _ => species.Id };
        Guid? breedId = scenario switch { "missing breed" => Guid.NewGuid(), "deleted breed" => deletedBreed.Id, "wrong species breed" => wrongBreed.Id, "missing species" or "deleted species" => null, _ => breed.Id };
        Guid? areaId = scenario switch { "missing area" => Guid.NewGuid(), "deleted area" => deletedArea.Id, _ => area.Id };
        var response = await client.PutAsJsonAsync($"/animal/{animal.Id}", new { revision = animal.Revision, name = "Changed", speciesId, breedId, areaId, quantity = 3, sex = 0, referenceId = "changed", color = "white", birthdate = "2021-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await client.GetStringAsync($"/animal/{animal.Id}"));
        Assert.Equal(history, await client.GetStringAsync($"/animal/{animal.Id}/history"));
    }

    [Fact]
    public async Task ExplicitNullOptionalsClearFieldsAndAdvanceRevision()
    {
        var species = await Species("Species");
        var breed = await Breed(species.Id, "Breed");
        var area = await Area("Area");
        var created = await client.PostAsJsonAsync("/animal", new { name = "Before", speciesId = species.Id, breedId = breed.Id, areaId = area.Id, quantity = 2, sex = 0, referenceId = "ref", color = "brown", birthdate = "2020-01-01" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var animal = (await created.Content.ReadFromJsonAsync<AddAnimalResponse>())!;
        var history = await client.GetStringAsync($"/animal/{animal.Id}/history");
        var response = await client.PutAsJsonAsync($"/animal/{animal.Id}", new { revision = animal.Revision, name = "After", speciesId = species.Id, breedId = (Guid?)null, areaId = (Guid?)null, quantity = 3, sex = 0, referenceId = (string?)null, color = (string?)null, birthdate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var current = (await client.GetFromJsonAsync<FarmStats.API.Features.Animal.GetAnimal.GetAnimalResponse>($"/animal/{animal.Id}"))!;
        Assert.Equal("After", current.Name);
        Assert.Equal(3, current.Quantity);
        Assert.Equal(species.Id, current.SpeciesId);
        Assert.NotEqual(animal.Revision, current.Revision);
        Assert.Null(current.BreedId);
        Assert.Null(current.AreaId);
        Assert.Null(current.ReferenceId);
        Assert.Null(current.Color);
        Assert.Null(current.Birthdate);
        // History includes the current revision, so compare events separately.
        var beforeHistory = System.Text.Json.JsonDocument.Parse(history);
        var afterHistory = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/animal/{animal.Id}/history"));
        Assert.Equal(beforeHistory.RootElement.GetProperty("items").GetRawText(), afterHistory.RootElement.GetProperty("items").GetRawText());
        Assert.Equal(beforeHistory.RootElement.GetProperty("totalCount").GetInt32(), afterHistory.RootElement.GetProperty("totalCount").GetInt32());
    }
}