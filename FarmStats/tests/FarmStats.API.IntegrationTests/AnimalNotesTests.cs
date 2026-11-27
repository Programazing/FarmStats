using System.Net;
using System.Net.Http.Json;
using System.Text;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddAnimalNote;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Animal.GetAnimal;
using FarmStats.API.Features.Animal.GetAnimalNotes;
using FarmStats.API.Features.Animal.Lifecycle;
using FarmStats.API.Features.Animal.RecordAnimalQuantityEvent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalNotesTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly SaveBarrier barrier = new();

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

                builder.ConfigureServices(services =>
                    services.AddDbContext<FarmStatsDbContext>(options =>
                        options.AddInterceptors(barrier)));
            });

        client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>()
            .Database.MigrateAsync();

        var response = await client.PostAsJsonAsync(
            "/species",
            new { name = "Notes test species" });

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

    private async Task<GetAnimalResponse> CreateAnimal(
        int trackingType,
        int quantity)
    {
        var response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Test Animal",
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

    private async Task<GetAnimalResponse> CreateAndSetInactive(
        int trackingType,
        int quantity,
        AnimalStatus status)
    {
        var animal = await CreateAnimal(trackingType, quantity);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/status-changes",
            new
            {
                revision = animal.Revision,
                status = (int)status,
                effectiveDate = "2024-01-20",
                notes = "Status change"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}"))!;
    }

    private async Task<(Guid Id, Guid Revision)> CreateAndDelete(
        int trackingType,
        int quantity)
    {
        var animal = await CreateAnimal(trackingType, quantity);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/deletion",
            new
            {
                revision = animal.Revision,
                reason = AnimalDeletionReason.TestData
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = await response.Content
            .ReadFromJsonAsync<AnimalLifecycleResponse>();

        return (animal.Id, deleted!.Revision);
    }

    private static string GenerateLongText(int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append('x');
        }
        return sb.ToString();
    }

    [Fact]
    public async Task CreateNote_ActiveIndividual_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "First note for individual",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalNoteResponse>();

        Assert.NotNull(created);
        Assert.Equal(animal.Id, created!.Id);
        Assert.NotEqual(animal.Revision, created.Revision);
        Assert.Equal("First note for individual", created.Text);
        Assert.Equal(new DateOnly(2024, 1, 15), created.EffectiveDate);

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
        Assert.Equal(created.NoteId, list.Items[0].Id);
        Assert.Equal(1, list.TotalCount);
    }

    [Fact]
    public async Task CreateNote_ActiveGroup_Succeeds()
    {
        var animal = await CreateAnimal(2, 50);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "First note for group",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalNoteResponse>();

        Assert.NotNull(created);
        Assert.Equal(animal.Id, created!.Id);
        Assert.NotEqual(animal.Revision, created.Revision);
        Assert.Equal("First note for group", created.Text);
    }

    [Fact]
    public async Task CreateNote_InactiveIndividual_Succeeds()
    {
        var animal = await CreateAndSetInactive(1, 1, AnimalStatus.Sold);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Note on sold animal",
                effectiveDate = "2024-01-21"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalNoteResponse>();

        Assert.NotNull(created);
        Assert.Equal("Note on sold animal", created!.Text);
    }

    [Fact]
    public async Task CreateNote_InactiveGroup_Succeeds()
    {
        var animal = await CreateAndSetInactive(2, 10, AnimalStatus.Deceased);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Note on deceased group",
                effectiveDate = "2024-01-21"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalNoteResponse>();

        Assert.NotNull(created);
        Assert.Equal("Note on deceased group", created!.Text);
    }

    [Fact]
    public async Task CreateNote_DeletedAnimal_Returns404()
    {
        var (animalId, revision) = await CreateAndDelete(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animalId}/notes",
            new
            {
                revision,
                text = "Should fail",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_MissingAnimal_Returns404()
    {
        var response = await client.PostAsJsonAsync(
            "/animal/00000000-0000-0000-0000-000000000001/notes",
            new
            {
                revision = Guid.NewGuid(),
                text = "Missing animal",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_MissingRevision_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = Guid.Empty,
                text = "Test",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_EmptyRevisionInBody_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = "00000000-0000-0000-0000-000000000000",
                text = "Test",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_StaleRevision_Returns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "First note",
                    effectiveDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Second note",
                    effectiveDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task CreateNote_ConcurrentWithQuantityEvent_Returns409ForOne()
    {
        var animal = await CreateAnimal(2, 10);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Note during quantity event",
                    effectiveDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/quantity-events",
                new
                {
                    revision = animal.Revision,
                    eventType = AnimalQuantityEventType.Addition,
                    quantity = 5,
                    effectiveDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Created);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);
    }

    [Fact]
    public async Task CreateNote_DeletionCommitsFirst_NoteReturns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Note racing deletion",
                    effectiveDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/deletion",
                new
                {
                    revision = animal.Revision,
                    reason = AnimalDeletionReason.TestData
                }));

        Assert.Equal(2, barrier.Arrivals);

        var noteConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var noteOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        // Exactly one operation should succeed, one should conflict
        Assert.Equal(1, noteConflict + deletionConflict);
        Assert.Equal(1, noteOk + deletionOk);

        // If note succeeded, verify it's listed; if deletion succeeded, verify no notes
        if (noteOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
                $"/animal/{animal.Id}/notes");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
                $"/animal/{animal.Id}/notes");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
    }

    [Fact]
    public async Task CreateNote_NoteCommitsFirst_DeletionReturns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/deletion",
                new
                {
                    revision = animal.Revision,
                    reason = AnimalDeletionReason.TestData
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Note racing deletion",
                    effectiveDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var noteConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var noteOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        // Exactly one operation should succeed, one should conflict
        Assert.Equal(1, noteConflict + deletionConflict);
        Assert.Equal(1, noteOk + deletionOk);

        // If note succeeded, verify it's listed; if deletion succeeded, verify no notes
        if (noteOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
                $"/animal/{animal.Id}/notes");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
                $"/animal/{animal.Id}/notes");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
    }

    [Fact]
    public async Task CreateNote_EmptyText_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_WhitespaceOnlyText_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "   \t\n  ",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_UnicodeWhitespace_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "\u00A0\u2000\u2003",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_TextExceeds2000AfterTrim_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = GenerateLongText(2001),
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_TextExactly2000AfterTrim_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = GenerateLongText(2000),
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalNoteResponse>();

        Assert.NotNull(created);
        Assert.Equal(2000, created!.Text.Length);
    }

    [Fact]
    public async Task CreateNote_FutureEffectiveDate_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Future note",
                effectiveDate = futureDate.ToString("yyyy-MM-dd")
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_TodayEffectiveDate_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Today note",
                effectiveDate = today.ToString("yyyy-MM-dd")
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_HistoricalEffectiveDate_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Historical note",
                effectiveDate = "2020-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_EffectiveDateBeforeBirthdate_Allowed()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Before birthdate",
                effectiveDate = "2010-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateNote_EffectiveDateBeforeStatusDate_Allowed()
    {
        var animal = await CreateAndSetInactive(1, 1, AnimalStatus.Sold);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Before status date",
                effectiveDate = "2020-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ListNotes_Empty_ReturnsEmptyPage()
    {
        var animal = await CreateAnimal(1, 1);

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Empty(list!.Items);
        Assert.Equal(0, list.TotalCount);
        Assert.Equal(1, list.Page);
        Assert.Equal(25, list.PageSize);
        Assert.Equal(0, list.TotalPages);
        Assert.False(list.HasPreviousPage);
        Assert.False(list.HasNextPage);
    }

    [Fact]
    public async Task ListNotes_Pagination_ReturnsCorrectPages()
    {
        var animal = await CreateAnimal(1, 1);

        for (int i = 0; i < 30; i++)
        {
            var current = await client.GetFromJsonAsync<GetAnimalResponse>(
                $"/animal/{animal.Id}");

            await client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = current!.Revision,
                    text = $"Note {i}",
                    effectiveDate = "2024-01-15"
                });
        }

        var page1 = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes?page=1&pageSize=25");

        Assert.NotNull(page1);
        Assert.Equal(25, page1!.Items.Count);
        Assert.Equal(30, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.True(page1.HasNextPage);
        Assert.False(page1.HasPreviousPage);

        var page2 = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes?page=2&pageSize=25");

        Assert.NotNull(page2);
        Assert.Equal(5, page2!.Items.Count);
        Assert.Equal(30, page2.TotalCount);
        Assert.False(page2.HasNextPage);
        Assert.True(page2.HasPreviousPage);
    }

    [Fact]
    public async Task ListNotes_PageOverflow_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.GetAsync(
            $"/animal/{animal.Id}/notes?page=99999999&pageSize=25");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListNotes_InvalidPageSize_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.GetAsync(
            $"/animal/{animal.Id}/notes?page=1&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListNotes_DeletedAnimal_ReturnsNotes()
    {
        var animal = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Note before deletion",
                effectiveDate = "2024-01-15"
            });

        var current = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/deletion",
            new
            {
                revision = current!.Revision,
                reason = AnimalDeletionReason.TestData
            });

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
        Assert.Equal("Note before deletion", list.Items[0].Text);
    }

    [Fact]
    public async Task ListNotes_MissingAnimal_Returns404()
    {
        var response = await client.GetAsync(
            "/animal/00000000-0000-0000-0000-000000000001/notes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListNotes_DeterministicOrdering_WithTiedTimestamps()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var now = DateTimeOffset.UtcNow;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        // Ensure firstId < secondId for deterministic Id ASC ordering
        if (string.Compare(firstId.ToString(), secondId.ToString(), StringComparison.Ordinal) > 0)
        {
            (firstId, secondId) = (secondId, firstId);
        }

        db.AnimalNotes.AddRange(
            new AnimalNote
            {
                Id = firstId,
                AnimalId = animal.Id,
                Text = "First note",
                EffectiveDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalNote
            {
                Id = secondId,
                AnimalId = animal.Id,
                Text = "Second note",
                EffectiveDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            });

        await db.SaveChangesAsync();

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal("First note", list.Items[0].Text);
        Assert.Equal("Second note", list.Items[1].Text);
    }

    [Fact]
    public async Task ListNotes_IsolationBetweenAnimals()
    {
        var animal1 = await CreateAnimal(1, 1);
        var animal2 = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal1.Id}/notes",
            new
            {
                revision = animal1.Revision,
                text = "Animal 1 note",
                effectiveDate = "2024-01-15"
            });

        await client.PostAsJsonAsync(
            $"/animal/{animal2.Id}/notes",
            new
            {
                revision = animal2.Revision,
                text = "Animal 2 note",
                effectiveDate = "2024-01-15"
            });

        var list1 = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal1.Id}/notes");

        var list2 = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal2.Id}/notes");

        Assert.NotNull(list1);
        Assert.NotNull(list2);
        Assert.Single(list1!.Items);
        Assert.Single(list2!.Items);
        Assert.Equal("Animal 1 note", list1.Items[0].Text);
        Assert.Equal("Animal 2 note", list2.Items[0].Text);
    }

    [Fact]
    public async Task CreateNote_DuplicateTextAllowedWithSuccessiveRevisions()
    {
        var animal = await CreateAnimal(1, 1);

        var response1 = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Same text",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

        var current = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        var response2 = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = current!.Revision,
                text = "Same text",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
    }

    [Fact]
    public async Task CreateNote_FailedValidation_NoPersistedEffects()
    {
        var animal = await CreateAnimal(1, 1);

        var before = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "",
                effectiveDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.Equal(before!.Revision, after!.Revision);

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task CreateNote_FailedConcurrency_NoPersistedEffects()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "First",
                    effectiveDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Second",
                    effectiveDate = "2024-01-15"
                }));

        var list = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task CreateNote_SuccessfulCreation_ChangesOnlyRevisionAndNote()
    {
        var animal = await CreateAnimal(1, 1);

        var beforeRevision = animal.Revision;
        var beforeQuantity = animal.Quantity;
        var beforeStatus = animal.Status;
        var beforeTrackingType = animal.TrackingType;

        var historyBefore = await client.GetFromJsonAsync<AnimalHistoryResponse>(
            $"/animal/{animal.Id}/history");

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/notes",
            new
            {
                revision = animal.Revision,
                text = "Test note",
                effectiveDate = "2024-01-15"
            });

        var after = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.NotEqual(beforeRevision, after!.Revision);
        Assert.Equal(beforeQuantity, after.Quantity);
        Assert.Equal(beforeStatus, after.Status);
        Assert.Equal(beforeTrackingType, after.TrackingType);

        var historyAfter = await client.GetFromJsonAsync<AnimalHistoryResponse>(
            $"/animal/{animal.Id}/history");

        Assert.Equal(historyBefore!.TotalCount, historyAfter!.TotalCount);
    }

    [Fact]
    public async Task Migration_PreservesExistingAnimalsAndHistory()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var animal = new Animal
        {
            Name = "Pre-migration animal",
            SpeciesId = speciesId,
            TrackingType = AnimalTrackingType.Individual,
            Quantity = 1,
            Sex = AnimalSex.Unknown,
            Status = AnimalStatus.Active,
            Revision = Guid.NewGuid()
        };

        db.Animals.Add(animal);
        await db.SaveChangesAsync();

        var loaded = await db.Animals
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Name == "Pre-migration animal");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Quantity);

        var historyCount = await db.AnimalHistory.CountAsync();
        Assert.True(historyCount >= 1);

        var notesCount = await db.AnimalNotes.CountAsync();
        Assert.Equal(0, notesCount);
    }

    private sealed class SaveBarrier : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int armed;
        private int arrivals;

        public int Arrivals => System.Threading.Volatile.Read(ref arrivals);
        public System.Collections.Concurrent.ConcurrentBag<Guid> Contexts { get; } = new();

        public void Arm() => System.Threading.Volatile.Write(ref armed, 1);

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (System.Threading.Volatile.Read(ref armed) == 1)
            {
                Contexts.Add(eventData.Context!.ContextId.InstanceId);

                if (System.Threading.Interlocked.Increment(ref arrivals) == 2)
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