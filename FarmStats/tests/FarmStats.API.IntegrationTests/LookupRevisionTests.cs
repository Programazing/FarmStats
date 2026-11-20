using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Species.GetSpeciesHistory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class LookupRevisionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
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

    private sealed class LookupResponse
    {
        public Guid Id { get; set; }
        public Guid Revision { get; set; }
        public string Name { get; set; } = "";
    }

    private async Task<LookupResponse> Create(string kind, string name, Guid? speciesId = null)
    {
        var response = await client.PostAsJsonAsync($"/{kind}", new { name, speciesId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LookupResponse>())!;
    }

    private async Task<List<LookupResponse>> List(string kind, Guid? parent)
    {
        var path = kind == "breed" ? $"/species/{parent}/breed" : $"/{kind}";
        return (await client.GetFromJsonAsync<List<LookupResponse>>(path))!;
    }

    private async Task<string> Snapshot(string kind, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
        ISoftDeletableLookup lookup = kind switch
        {
            "species" => await db.Species.IgnoreQueryFilters().SingleAsync(x => x.Id == id),
            "breed" => await db.Breeds.IgnoreQueryFilters().SingleAsync(x => x.Id == id),
            _ => await db.Areas.IgnoreQueryFilters().SingleAsync(x => x.Id == id)
        };
        var history = await db.LookupHistory.Where(x => x.LookupId == id).OrderBy(x => x.Id).ToListAsync();
        return System.Text.Json.JsonSerializer.Serialize(new { lookup.Name, lookup.Revision, lookup.DeletedAtUtc, lookup.DeletionReason, lookup.DeletionNotes, history });
    }

    [Theory]
    [InlineData("species")]
    [InlineData("breed")]
    [InlineData("area")]
    public async Task RevisionsProtectLifecycleAndHistory(string kind)
    {
        var parent = kind == "breed" ? (await Create("species", "Parent")).Id : (Guid?)null;
        var created = await Create(kind, "Original", parent);
        Assert.NotEqual(Guid.Empty, created.Revision);
        var path = $"/{kind}/{created.Id}";
        var updatePath = kind == "area" ? $"/areas/{created.Id}" : path;
        Assert.Contains(await List(kind, parent), x => x.Id == created.Id);
        var snapshot = await Snapshot(kind, created.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(updatePath, new { name = "Invalid", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(updatePath, new { revision = Guid.Empty, name = "Invalid", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/deletion", new { reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/deletion", new { revision = Guid.Empty, reason = 1 })).StatusCode);
        Assert.Equal(snapshot, await Snapshot(kind, created.Id));
        var update = await client.PutAsJsonAsync(updatePath, new { revision = created.Revision, name = " Renamed ", speciesId = parent });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<LookupResponse>())!;
        Assert.Equal("Renamed", updated.Name);
        Assert.NotEqual(created.Revision, updated.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(updatePath, new { revision = created.Revision, name = "Stale", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/deletion", new { revision = created.Revision, reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/deletion", new { revision = Guid.Empty, reason = 1 })).StatusCode);
        var before = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history"))!;
        Assert.Equal(updated.Revision, before.Revision);
        Assert.Equal(0, before.TotalCount);

        var reloaded = Assert.Single(await List(kind, parent), x => x.Id == created.Id);
        Assert.Equal(updated.Revision, reloaded.Revision);
        var retry = await client.PutAsJsonAsync(updatePath, new { revision = reloaded.Revision, name = "Renamed", speciesId = parent });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        updated = (await retry.Content.ReadFromJsonAsync<LookupResponse>())!;

        var deletion = await client.PostAsJsonAsync($"{path}/deletion", new { revision = updated.Revision, reason = 1, notes = " deleted " });
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        var deleted = (await deletion.Content.ReadFromJsonAsync<LookupResponse>())!;
        Assert.NotEqual(updated.Revision, deleted.Revision);
        Assert.DoesNotContain(await List(kind, parent), x => x.Id == created.Id);
        snapshot = await Snapshot(kind, created.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/restore", new { notes = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/restore", new { revision = Guid.Empty })).StatusCode);
        Assert.Equal(snapshot, await Snapshot(kind, created.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(updatePath, new { revision = deleted.Revision, name = "Hidden", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/restore", new { revision = updated.Revision })).StatusCode);
        var history = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history"))!;
        Assert.Equal(deleted.Revision, history.Revision);
        var entry = Assert.Single(history.Items);
        Assert.Equal(LookupHistoryEventType.Deleted, entry.EventType);
        Assert.Equal("Renamed", entry.NameSnapshot);
        Assert.Equal("deleted", entry.Notes);
        Assert.Equal(LookupDeletionReason.DuplicateRecord, entry.DeletionReason);

        var restore = await client.PostAsJsonAsync($"{path}/restore", new { revision = history.Revision, notes = " restored " });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = (await restore.Content.ReadFromJsonAsync<LookupResponse>())!;
        Assert.NotEqual(deleted.Revision, restored.Revision);
        Assert.Contains(await List(kind, parent), x => x.Id == created.Id && x.Revision == restored.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/restore", new { revision = restored.Revision })).StatusCode);
        history = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history"))!;
        Assert.Equal(restored.Revision, history.Revision);
        Assert.Equal(2, history.TotalCount);
        Assert.Contains(history.Items, item => item.EventType == LookupHistoryEventType.Restored && item.Notes == "restored" && item.NameSnapshot == "Renamed");
        var page = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history?page=2&pageSize=1"))!;
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{path}/history?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/{kind}/{Guid.NewGuid()}/history")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{path}/deletion", new { revision = restored.Revision, reason = 1 })).StatusCode);
        Guid[] expected;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FarmStatsDbContext>();
            var entries = await db.LookupHistory.Where(x => x.LookupId == created.Id).ToListAsync();
            Assert.Equal(3, entries.Count);
            var ordered = entries.OrderBy(x => x.Id.ToString()).ToArray();
            var older = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
            var tied = DateTimeOffset.Parse("2021-01-01T00:00:00Z");
            await db.LookupHistory.Where(x => x.Id == ordered[0].Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RecordedAtUtc, older));
            await db.LookupHistory.Where(x => x.Id == ordered[1].Id || x.Id == ordered[2].Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RecordedAtUtc, tied));
            expected = [ordered[1].Id, ordered[2].Id, ordered[0].Id];
        }
        history = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history"))!;
        Assert.Equal(expected, history.Items.Select(x => x.Id));
        for (var i = 0; i < expected.Length; i++)
        {
            page = (await client.GetFromJsonAsync<HistoryResponse>($"{path}/history?page={i + 1}&pageSize=1"))!;
            Assert.Equal(expected[i], Assert.Single(page.Items).Id);
        }
    }

    [Theory]
    [InlineData("species")]
    [InlineData("breed")]
    [InlineData("area")]
    public async Task NormalizedNamesRemainUniqueForActiveAndDeletedLookups(string kind)
    {
        var parent = kind == "breed" ? (await Create("species", "Parent")).Id : (Guid?)null;
        var original = await Create(kind, "Reserved Name", parent);
        var other = await Create(kind, "Other Name", parent);
        var updatePath = kind == "area" ? $"/areas/{other.Id}" : $"/{kind}/{other.Id}";
        var originalPath = kind == "area" ? $"/areas/{original.Id}" : $"/{kind}/{original.Id}";
        var selfRename = await client.PutAsJsonAsync(originalPath, new { revision = original.Revision, name = " reserved NAME ", speciesId = parent });
        Assert.Equal(HttpStatusCode.OK, selfRename.StatusCode);
        original = (await selfRename.Content.ReadFromJsonAsync<LookupResponse>())!;
        Assert.Equal("reserved NAME", original.Name);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/{kind}", new { name = " reserved NAME ", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(updatePath, new { revision = other.Revision, name = " reserved NAME ", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/{kind}/{original.Id}/deletion", new { revision = original.Revision, reason = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/{kind}", new { name = " reserved NAME ", speciesId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(updatePath, new { revision = other.Revision, name = " reserved NAME ", speciesId = parent })).StatusCode);
        Assert.Equal("Other Name", Assert.Single(await List(kind, parent), x => x.Id == other.Id).Name);
        var history = (await client.GetFromJsonAsync<HistoryResponse>($"/{kind}/{other.Id}/history"))!;
        Assert.Equal(other.Revision, history.Revision);
        Assert.Equal(0, history.TotalCount);
        if (kind == "breed")
        {
            var otherParent = await Create("species", "Other parent");
            await Create("breed", " reserved NAME ", otherParent.Id);
        }
    }
}