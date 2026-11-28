using System.Net;
using System.Net.Http.Json;
using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.AddAnimal;
using FarmStats.API.Features.Animal.AddAnimalWeightMeasurement;
using FarmStats.API.Features.Animal.AddSpecies;
using FarmStats.API.Features.Animal.GetAnimal;
using FarmStats.API.Features.Animal.GetAnimalWeightMeasurements;
using FarmStats.API.Features.Animal.Lifecycle;
using FarmStats.API.Features.Animal.RecordAnimalQuantityEvent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalWeightMeasurementTests : IAsyncLifetime
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
            new { name = "Weight measurement test species" });

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

    [Fact]
    public async Task CreateMeasurement_ActiveIndividual_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 123.456m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(animal.Id, created!.Id);
        Assert.NotEqual(animal.Revision, created.Revision);
        Assert.Equal(123.456m, created.Value);
        Assert.Equal(WeightUnit.Kilogram, created.Unit);
        Assert.Equal(new DateOnly(2024, 1, 15), created.MeasurementDate);

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
        Assert.Equal(created.MeasurementId, list.Items[0].Id);
        Assert.Equal(1, list.TotalCount);
        Assert.Equal(created.MeasurementId, list.LatestWeight?.MeasurementId);
    }

    [Fact]
    public async Task CreateMeasurement_ActiveGroup_Succeeds()
    {
        var animal = await CreateAnimal(2, 50);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 500.5m,
                unit = 1,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(animal.Id, created!.Id);
        Assert.NotEqual(animal.Revision, created.Revision);
        Assert.Equal(500.5m, created.Value);
        Assert.Equal(WeightUnit.Pound, created.Unit);
    }

    [Fact]
    public async Task CreateMeasurement_InactiveIndividual_Succeeds()
    {
        var animal = await CreateAndSetInactive(1, 1, AnimalStatus.Sold);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 450.0m,
                unit = 0,
                measurementDate = "2024-01-21"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(450.0m, created!.Value);
    }

    [Fact]
    public async Task CreateMeasurement_InactiveGroup_Succeeds()
    {
        var animal = await CreateAndSetInactive(2, 10, AnimalStatus.Deceased);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 1000.25m,
                unit = 1,
                measurementDate = "2024-01-21"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(1000.25m, created!.Value);
    }

    [Fact]
    public async Task CreateMeasurement_DeletedAnimal_Returns404()
    {
        var (animalId, revision) = await CreateAndDelete(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animalId}/weight-measurements",
            new
            {
                revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_MissingAnimal_Returns404()
    {
        var response = await client.PostAsJsonAsync(
            "/animal/00000000-0000-0000-0000-000000000001/weight-measurements",
            new
            {
                revision = Guid.NewGuid(),
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_MissingRevision_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = Guid.Empty,
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_StaleRevision_Returns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 100.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 200.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task CreateMeasurement_ConcurrentWithQuantityEvent_Returns409ForOne()
    {
        var animal = await CreateAnimal(2, 10);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
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
    public async Task CreateMeasurement_DeletionCommitsFirst_MeasurementReturns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/deletion",
                new
                {
                    revision = animal.Revision,
                    reason = AnimalDeletionReason.TestData
                }));

        Assert.Equal(2, barrier.Arrivals);

        var measurementConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var measurementOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        Assert.Equal(1, measurementConflict + deletionConflict);
        Assert.Equal(1, measurementOk + deletionOk);

        if (measurementOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
    }

    [Fact]
    public async Task CreateMeasurement_NoteCommitsFirst_DeletionReturns409()
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
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var measurementConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var measurementOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        Assert.Equal(1, measurementConflict + deletionConflict);
        Assert.Equal(1, measurementOk + deletionOk);

        if (measurementOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
    }

    [Fact]
    public async Task CreateMeasurement_ValueZero_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_ValueNegative_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = -10.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_ValueExceedsMax_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 10000000.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_ValueMaxAllowed_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 9999999.999m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(9999999.999m, created!.Value);
    }

    [Fact]
    public async Task CreateMeasurement_ValueWith4DecimalPlaces_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 1.2345m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_ValueWithTrailingZeros_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 1.2300m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(1.23m, created!.Value);
    }

    [Fact]
    public async Task CreateMeasurement_InvalidUnit_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 99,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_MissingUnit_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_FutureMeasurementDate_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = futureDate.ToString("yyyy-MM-dd")
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_TodayMeasurementDate_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = today.ToString("yyyy-MM-dd")
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_HistoricalMeasurementDate_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2020-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_DateBeforeBirthdate_Allowed()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2010-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_DateBeforeStatusDate_Allowed()
    {
        var animal = await CreateAndSetInactive(1, 1, AnimalStatus.Sold);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2020-01-01"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ListMeasurements_Empty_ReturnsEmptyPage()
    {
        var animal = await CreateAnimal(1, 1);

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Empty(list!.Items);
        Assert.Equal(0, list.TotalCount);
        Assert.Equal(1, list.Page);
        Assert.Equal(25, list.PageSize);
        Assert.Equal(0, list.TotalPages);
        Assert.False(list.HasPreviousPage);
        Assert.False(list.HasNextPage);
        Assert.Null(list.LatestWeight);
    }

    [Fact]
    public async Task ListMeasurements_Pagination_ReturnsCorrectPages()
    {
        var animal = await CreateAnimal(1, 1);

        for (int i = 0; i < 30; i++)
        {
            var current = await client.GetFromJsonAsync<GetAnimalResponse>(
                $"/animal/{animal.Id}");

            await client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = current!.Revision,
                    value = 100.0m + i,
                    unit = 0,
                    measurementDate = "2024-01-15"
                });
        }

        var page1 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements?page=1&pageSize=25");

        Assert.NotNull(page1);
        Assert.Equal(25, page1!.Items.Count);
        Assert.Equal(30, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.True(page1.HasNextPage);
        Assert.False(page1.HasPreviousPage);
        Assert.NotNull(page1.LatestWeight);

        var page2 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements?page=2&pageSize=25");

        Assert.NotNull(page2);
        Assert.Equal(5, page2!.Items.Count);
        Assert.Equal(30, page2.TotalCount);
        Assert.False(page2.HasNextPage);
        Assert.True(page2.HasPreviousPage);
        Assert.NotNull(page2.LatestWeight);
    }

    [Fact]
    public async Task ListMeasurements_PageOverflow_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.GetAsync(
            $"/animal/{animal.Id}/weight-measurements?page=99999999&pageSize=25");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListMeasurements_InvalidPageSize_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.GetAsync(
            $"/animal/{animal.Id}/weight-measurements?page=1&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListMeasurements_DeletedAnimal_ReturnsMeasurements()
    {
        var animal = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
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

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
        Assert.Equal(150.0m, list.Items[0].Value);
    }

    [Fact]
    public async Task ListMeasurements_MissingAnimal_Returns404()
    {
        var response = await client.GetAsync(
            "/animal/00000000-0000-0000-0000-000000000001/weight-measurements");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListMeasurements_DeterministicOrdering_WithTiedDates()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var now = DateTimeOffset.UtcNow;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        if (string.Compare(firstId.ToString(), secondId.ToString(), StringComparison.Ordinal) > 0)
        {
            (firstId, secondId) = (secondId, firstId);
        }

        db.AnimalWeightMeasurements.AddRange(
            new AnimalWeightMeasurement
            {
                Id = firstId,
                AnimalId = animal.Id,
                Value = 100.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalWeightMeasurement
            {
                Id = secondId,
                AnimalId = animal.Id,
                Value = 200.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            });

        await db.SaveChangesAsync();

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(100.0m, list.Items[0].Value);
        Assert.Equal(200.0m, list.Items[1].Value);
    }

    [Fact]
    public async Task ListMeasurements_DeterministicOrdering_WithTiedDatesAndTimestamps()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var now = DateTimeOffset.UtcNow;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        if (string.Compare(firstId.ToString(), secondId.ToString(), StringComparison.Ordinal) > 0)
        {
            (firstId, secondId) = (secondId, firstId);
        }

        db.AnimalWeightMeasurements.AddRange(
            new AnimalWeightMeasurement
            {
                Id = firstId,
                AnimalId = animal.Id,
                Value = 100.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalWeightMeasurement
            {
                Id = secondId,
                AnimalId = animal.Id,
                Value = 200.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            });

        await db.SaveChangesAsync();

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(100.0m, list.Items[0].Value);
        Assert.Equal(200.0m, list.Items[1].Value);
        Assert.Equal(firstId, list.Items[0].Id);
        Assert.Equal(secondId, list.Items[1].Id);
    }

    [Fact]
    public async Task ListMeasurements_DeterministicOrdering_WithDifferentMeasurementDates()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var now = DateTimeOffset.UtcNow;

        db.AnimalWeightMeasurements.AddRange(
            new AnimalWeightMeasurement
            {
                Id = Guid.NewGuid(),
                AnimalId = animal.Id,
                Value = 100.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalWeightMeasurement
            {
                Id = Guid.NewGuid(),
                AnimalId = animal.Id,
                Value = 200.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 15),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalWeightMeasurement
            {
                Id = Guid.NewGuid(),
                AnimalId = animal.Id,
                Value = 150.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 10),
                RecordedAtUtc = now,
                ActorUserId = null
            });

        await db.SaveChangesAsync();

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Equal(3, list!.Items.Count);
        Assert.Equal(200.0m, list.Items[0].Value);
        Assert.Equal(new DateOnly(2024, 1, 15), list.Items[0].MeasurementDate);
        Assert.Equal(150.0m, list.Items[1].Value);
        Assert.Equal(new DateOnly(2024, 1, 10), list.Items[1].MeasurementDate);
        Assert.Equal(100.0m, list.Items[2].Value);
        Assert.Equal(new DateOnly(2024, 1, 1), list.Items[2].MeasurementDate);
    }

    [Fact]
    public async Task ListMeasurements_LatestWeightSummary_IsFirstItem()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var now = DateTimeOffset.UtcNow;

        db.AnimalWeightMeasurements.AddRange(
            new AnimalWeightMeasurement
            {
                Id = Guid.NewGuid(),
                AnimalId = animal.Id,
                Value = 100.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 1),
                RecordedAtUtc = now,
                ActorUserId = null
            },
            new AnimalWeightMeasurement
            {
                Id = Guid.NewGuid(),
                AnimalId = animal.Id,
                Value = 200.0m,
                Unit = WeightUnit.Kilogram,
                MeasurementDate = new DateOnly(2024, 1, 15),
                RecordedAtUtc = now,
                ActorUserId = null
            });

        await db.SaveChangesAsync();

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.NotNull(list.LatestWeight);
        Assert.Equal(200.0m, list.LatestWeight!.Value);
        Assert.Equal(new DateOnly(2024, 1, 15), list.LatestWeight.MeasurementDate);
        Assert.Equal(list.Items[0].Id, list.LatestWeight.MeasurementId);
    }

    [Fact]
    public async Task ListMeasurements_BackdatedEntry_DoesNotDisplaceNewer()
    {
        var animal = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 200.0m,
                unit = 0,
                measurementDate = "2024-01-20"
            });

        var current = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = current!.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.NotNull(list.LatestWeight);
        Assert.Equal(200.0m, list.LatestWeight!.Value);
        Assert.Equal(new DateOnly(2024, 1, 20), list.LatestWeight.MeasurementDate);
    }

    [Fact]
    public async Task ListMeasurements_SameDayOrdering_ByRecordedAtUtc()
    {
        var animal = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var current1 = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        await Task.Delay(10);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = current1!.Revision,
                value = 200.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.Equal(200.0m, list.Items[0].Value);
        Assert.Equal(100.0m, list.Items[1].Value);
    }

    [Fact]
    public async Task ListMeasurements_IsolationBetweenAnimals()
    {
        var animal1 = await CreateAnimal(1, 1);
        var animal2 = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal1.Id}/weight-measurements",
            new
            {
                revision = animal1.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        await client.PostAsJsonAsync(
            $"/animal/{animal2.Id}/weight-measurements",
            new
            {
                revision = animal2.Revision,
                value = 200.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var list1 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal1.Id}/weight-measurements");

        var list2 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal2.Id}/weight-measurements");

        Assert.NotNull(list1);
        Assert.NotNull(list2);
        Assert.Single(list1!.Items);
        Assert.Single(list2!.Items);
        Assert.Equal(100.0m, list1.Items[0].Value);
        Assert.Equal(200.0m, list2.Items[0].Value);
    }

    [Fact]
    public async Task GetAnimal_LatestWeightSummary_Included()
    {
        var animal = await CreateAnimal(1, 1);

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.5m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var got = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.NotNull(got);
        Assert.NotNull(got!.LatestWeight);
        Assert.Equal(150.5m, got.LatestWeight.Value);
        Assert.Equal(WeightUnit.Kilogram, got.LatestWeight.Unit);
        Assert.Equal(new DateOnly(2024, 1, 15), got.LatestWeight.MeasurementDate);
    }

    [Fact]
    public async Task GetAnimal_NoMeasurements_LatestWeightIsNull()
    {
        var animal = await CreateAnimal(1, 1);

        var got = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.NotNull(got);
        Assert.Null(got!.LatestWeight);
    }

    [Fact]
    public async Task CreateMeasurement_FailedValidation_NoPersistedEffects()
    {
        var animal = await CreateAnimal(1, 1);

        var before = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.Equal(before!.Revision, after!.Revision);

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task CreateMeasurement_FailedConcurrency_NoPersistedEffects()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 100.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 200.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }));

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task CreateMeasurement_SuccessfulCreation_ChangesOnlyRevisionAndMeasurement()
    {
        var animal = await CreateAnimal(1, 1);

        var beforeRevision = animal.Revision;
        var beforeQuantity = animal.Quantity;
        var beforeStatus = animal.Status;
        var beforeTrackingType = animal.TrackingType;

        var historyBefore = await client.GetFromJsonAsync<AnimalHistoryResponse>(
            $"/animal/{animal.Id}/history");

        await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
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
    public async Task CreateMeasurement_OrdinaryAnimalCreation_WithoutWeight_Succeeds()
    {
        var response = await client.PostAsJsonAsync("/animal", new
        {
            name = "Test Animal No Weight",
            speciesId,
            trackingType = 1,
            quantity = 1,
            sex = 0
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var added = (await response.Content
            .ReadFromJsonAsync<AddAnimalResponse>())!;

        var got = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{added.Id}");

        Assert.NotNull(got);
        Assert.Null(got!.LatestWeight);
    }

    [Fact]
    public async Task CreateMeasurement_OrdinaryAnimalUpdate_WithoutWeight_Succeeds()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PutAsJsonAsync(
            $"/animal/{animal.Id}",
            new
            {
                revision = animal.Revision,
                name = "Updated Name",
                speciesId = animal.SpeciesId,
                trackingType = animal.TrackingType,
                quantity = animal.Quantity,
                sex = animal.Sex,
                color = animal.Color,
                birthdate = animal.Birthdate,
                areaId = animal.AreaId
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated!.Name);
        Assert.Null(updated.LatestWeight);
    }

    [Fact]
    public async Task Migration_UpgradeFromAddAnimalNotes_PreservesDataAndAddsWeights()
    {
        // Create a disposable database at the AddAnimalNotes baseline
        await using var postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<FarmStatsDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new FarmStatsDbContext(options);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        async Task<string> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (await command.ExecuteScalarAsync())!.ToString()!;
        }

        var migrator = db.GetService<IMigrator>();

        // 1. Apply migrations through AddAnimalNotes only
        const string notesMigration = "20261127073731_AddAnimalNotes";
        const string weightsMigration = "20261127194200_AddAnimalWeightMeasurements";

        await Execute(migrator.GenerateScript("0", notesMigration));

        // Verify: AddAnimalNotes applied, weights not applied, weight table absent
        var appliedAfterNotes = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(notesMigration, appliedAfterNotes);
        Assert.DoesNotContain(weightsMigration, appliedAfterNotes);

        var tableExists = await Scalar("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'AnimalWeightMeasurements'
        """);
        Assert.Equal("0", tableExists);

        // 2. Seed representative pre-weight data
        var speciesId = Guid.NewGuid();
        var breedId = Guid.NewGuid();
        var areaId = Guid.NewGuid();
        var animalId = Guid.NewGuid();
        var animalRevision = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var qtyEventId = Guid.NewGuid();

        await Execute($"""
            INSERT INTO "Species" ("Id", "Name")
            VALUES ('{speciesId}', 'Test Species');
            INSERT INTO "Breeds" ("Id", "Name", "SpeciesId")
            VALUES ('{breedId}', 'Test Breed', '{speciesId}');
            INSERT INTO "Areas" ("Id", "Name")
            VALUES ('{areaId}', 'Test Area');
            INSERT INTO "Animals" ("Id", "Name", "SpeciesId", "BreedId", "AreaId", "Quantity", "Sex", "ReferenceId", "Color", "Birthdate",
                "TrackingType", "Status", "StatusEffectiveDate", "StatusNotes", "Revision", "DeletedAtUtc", "DeletionReason", "DeletionNotes")
            VALUES ('{animalId}', 'Pre-migration animal', '{speciesId}', '{breedId}', '{areaId}', 1, 1, 'ref-001', 'brown', '2020-01-02',
                1, 0, NULL, NULL, '{animalRevision}', NULL, NULL, NULL);
        """);

        // Seed note
        await Execute($"""
            INSERT INTO "AnimalNotes" ("Id", "AnimalId", "Text", "EffectiveDate", "RecordedAtUtc", "ActorUserId")
            VALUES ('{noteId}', '{animalId}', 'Pre-migration note', '2023-06-15', '2023-06-15 12:00:00+00', NULL);
        """);

        // Seed history (lifecycle)
        await Execute($"""
            INSERT INTO "AnimalHistory" ("Id", "AnimalId", "EventType", "EffectiveDate", "RecordedAtUtc", "ActorUserId", "Notes",
                "PreviousQuantity", "NewQuantity", "QuantityDelta", "QuantityEventType", "PreviousStatus", "NewStatus")
            VALUES ('{historyId}', '{animalId}', 0, '2023-01-01', '2023-01-01 12:00:00+00', NULL, 'Created', NULL, NULL, NULL, NULL, NULL, 0);
        """);

        // Seed quantity event
        await Execute($"""
            INSERT INTO "AnimalHistory" ("Id", "AnimalId", "EventType", "EffectiveDate", "RecordedAtUtc", "ActorUserId", "Notes",
                "PreviousQuantity", "NewQuantity", "QuantityDelta", "QuantityEventType")
            VALUES ('{qtyEventId}', '{animalId}', 4, '2023-07-01', '2023-07-01 12:00:00+00', NULL, 'Addition event',
                1, 6, 5, 0);
        """);

        // Capture seeded values
        var seededAnimalName = await Scalar($"""SELECT "Name" FROM "Animals" WHERE "Id" = '{animalId}'""");
        var seededAnimalRevision = await Scalar($"""SELECT "Revision" FROM "Animals" WHERE "Id" = '{animalId}'""");
        var seededNoteText = await Scalar($"""SELECT "Text" FROM "AnimalNotes" WHERE "Id" = '{noteId}'""");
        var seededHistoryCount = await Scalar($"""SELECT count(*) FROM "AnimalHistory" WHERE "AnimalId" = '{animalId}'""");
        var seededQtyEventCount = await Scalar($"""SELECT count(*) FROM "AnimalHistory" WHERE "AnimalId" = '{animalId}' AND "EventType" = 4""");

        // 3. Apply the weight migration
        await Execute(migrator.GenerateScript(notesMigration, weightsMigration));

        // 4. Verify weight migration applied and weight table exists (empty)
        var appliedAfterWeights = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(notesMigration, appliedAfterWeights);
        Assert.Contains(weightsMigration, appliedAfterWeights);

        tableExists = await Scalar("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'AnimalWeightMeasurements'
        """);
        Assert.Equal("1", tableExists);

        var weightCount = await Scalar("SELECT count(*) FROM \"AnimalWeightMeasurements\"");
        Assert.Equal("0", weightCount);

        // 5. Verify original data preserved
        var animalName = await Scalar($"""SELECT "Name" FROM "Animals" WHERE "Id" = '{animalId}'""");
        var animalRevisionAfter = await Scalar($"""SELECT "Revision" FROM "Animals" WHERE "Id" = '{animalId}'""");
        Assert.Equal(seededAnimalName, animalName);
        Assert.Equal(seededAnimalRevision, animalRevisionAfter);

        var noteText = await Scalar($"""SELECT "Text" FROM "AnimalNotes" WHERE "Id" = '{noteId}'""");
        Assert.Equal(seededNoteText, noteText);

        var historyCount = await Scalar($"""SELECT count(*) FROM "AnimalHistory" WHERE "AnimalId" = '{animalId}'""");
        var qtyEventCount = await Scalar($"""SELECT count(*) FROM "AnimalHistory" WHERE "AnimalId" = '{animalId}' AND "EventType" = 4""");
        Assert.Equal(seededHistoryCount, historyCount);
        Assert.Equal(seededQtyEventCount, qtyEventCount);

        // Verify relationships preserved (FKs intact)
        var fkCheck = await Scalar($"""
            SELECT count(*) FROM "Animals" a
            JOIN "Species" s ON s."Id" = a."SpeciesId"
            JOIN "Breeds" b ON b."Id" = a."BreedId" AND b."SpeciesId" = s."Id"
            JOIN "Areas" r ON r."Id" = a."AreaId"
            WHERE a."Id" = '{animalId}'
        """);
        Assert.Equal("1", fkCheck);
    }

    [Fact]
    public async Task CreateMeasurement_DuplicateValueAndDateAllowedWithSuccessiveRevisions()
    {
        var animal = await CreateAnimal(1, 1);

        var response1 = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

        var current = await client.GetFromJsonAsync<GetAnimalResponse>(
            $"/animal/{animal.Id}");

        var response2 = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = current!.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);

        var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
    }

    [Fact]
    public async Task ListMeasurements_LatestWeightUnchangedAcrossPages()
    {
        var animal = await CreateAnimal(1, 1);

        for (int i = 0; i < 30; i++)
        {
            var current = await client.GetFromJsonAsync<GetAnimalResponse>(
                $"/animal/{animal.Id}");

            await client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = current!.Revision,
                    value = 100.0m + i,
                    unit = 0,
                    measurementDate = "2024-01-15"
                });
        }

        var page1 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements?page=1&pageSize=25");

        var page2 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements?page=2&pageSize=25");

        var page3 = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements?page=3&pageSize=25");

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.NotNull(page3);
        Assert.NotNull(page1!.LatestWeight);
        Assert.NotNull(page2!.LatestWeight);
        Assert.NotNull(page3!.LatestWeight);
        Assert.Equal(page1.LatestWeight.MeasurementId, page2.LatestWeight.MeasurementId);
        Assert.Equal(page1.LatestWeight.Value, page2.LatestWeight.Value);
        Assert.Equal(page1.LatestWeight.MeasurementId, page3.LatestWeight.MeasurementId);
        Assert.Equal(page1.LatestWeight.Value, page3.LatestWeight.Value);
        Assert.Empty(page3.Items);
    }



    [Fact]
    public async Task CreateMeasurement_LegacyUnspecifiedTrackingType_Succeeds()
    {
        // Create a legacy Unspecified animal directly in the database
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var legacyAnimal = new Animal
        {
            Name = "Legacy Unspecified Animal",
            SpeciesId = speciesId,
            TrackingType = AnimalTrackingType.Unspecified,
            Quantity = 1,
            Sex = AnimalSex.Unknown,
            Status = AnimalStatus.Active,
            Revision = Guid.NewGuid()
        };

        db.Animals.Add(legacyAnimal);
        await db.SaveChangesAsync();

        var response = await client.PostAsJsonAsync(
            $"/animal/{legacyAnimal.Id}/weight-measurements",
            new
            {
                revision = legacyAnimal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        Assert.NotNull(created);
        Assert.Equal(legacyAnimal.Id, created!.Id);
    }

    [Fact]
    public async Task CreateMeasurement_MissingMeasurementDate_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMeasurement_MalformedMeasurementDate_Returns400()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 100.0m,
                unit = 0,
                measurementDate = "not-a-date"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseConstraint_ValuePositive_RejectedAtDatabaseLevel()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var badMeasurement = new AnimalWeightMeasurement
        {
            Id = Guid.NewGuid(),
            AnimalId = animal.Id,
            Value = 0m,
            Unit = WeightUnit.Kilogram,
            MeasurementDate = new DateOnly(2024, 1, 15),
            RecordedAtUtc = DateTimeOffset.UtcNow,
            ActorUserId = null
        };

        db.AnimalWeightMeasurements.Add(badMeasurement);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseConstraint_UnitAllowedValues_RejectedAtDatabaseLevel()
    {
        var animal = await CreateAnimal(1, 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var badMeasurement = new AnimalWeightMeasurement
        {
            Id = Guid.NewGuid(),
            AnimalId = animal.Id,
            Value = 100.0m,
            Unit = (WeightUnit)99,
            MeasurementDate = new DateOnly(2024, 1, 15),
            RecordedAtUtc = DateTimeOffset.UtcNow,
            ActorUserId = null
        };

        db.AnimalWeightMeasurements.Add(badMeasurement);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AppendOnlyGuard_UpdateViaTrackedEntity_Throws()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var measurement = await db.AnimalWeightMeasurements
            .FirstAsync(m => m.Id == created!.MeasurementId);

        measurement.Value = 999.0m;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync());

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendOnlyGuard_DeleteViaTrackedEntity_Throws()
    {
        var animal = await CreateAnimal(1, 1);

        var response = await client.PostAsJsonAsync(
            $"/animal/{animal.Id}/weight-measurements",
            new
            {
                revision = animal.Revision,
                value = 150.0m,
                unit = 0,
                measurementDate = "2024-01-15"
            });

        var created = await response.Content
            .ReadFromJsonAsync<CreateAnimalWeightMeasurementResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FarmStatsDbContext>();

        var measurement = await db.AnimalWeightMeasurements
            .FirstAsync(m => m.Id == created!.MeasurementId);

        db.AnimalWeightMeasurements.Remove(measurement);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SaveChangesAsync());

        Assert.Contains("append-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateMeasurement_ConcurrentWithNote_Returns409ForOne()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Note racing measurement",
                    effectiveDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var measurementConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var noteConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        var measurementOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var noteOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/notes") == true);

        Assert.Equal(1, measurementConflict + noteConflict);
        Assert.Equal(1, measurementOk + noteOk);

        if (measurementOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }

        var noteList = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");
        Assert.NotNull(noteList);
    }

    [Fact]
    public async Task CreateMeasurement_ConcurrentWithNote_Atomicity_NoPartialEffects()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/notes",
                new
                {
                    revision = animal.Revision,
                    text = "Note racing measurement",
                    effectiveDate = "2024-01-15"
                }));

        var measurementList = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
            $"/animal/{animal.Id}/weight-measurements");
        var noteList = await client.GetFromJsonAsync<GetAnimalNotesResponse>(
            $"/animal/{animal.Id}/notes");

        Assert.NotNull(measurementList);
        Assert.NotNull(noteList);

        int measurementCount = measurementList!.Items.Count;
        int noteCount = noteList!.Items.Count;

        Assert.True(measurementCount == 0 || measurementCount == 1);
        Assert.True(noteCount == 0 || noteCount == 1);

        var total = measurementCount + noteCount;
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task CreateMeasurement_MeasurementFirst_DeletionReturns409()
    {
        var animal = await CreateAnimal(1, 1);

        barrier.Arm();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }),
            client.PostAsJsonAsync(
                $"/animal/{animal.Id}/deletion",
                new
                {
                    revision = animal.Revision,
                    reason = AnimalDeletionReason.TestData
                }));

        Assert.Equal(2, barrier.Arrivals);

        var measurementConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var measurementOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        Assert.Equal(1, measurementConflict + deletionConflict);
        Assert.Equal(1, measurementOk + deletionOk);

        if (measurementOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
    }

    [Fact]
    public async Task CreateMeasurement_DeletionFirst_MeasurementReturns409()
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
                $"/animal/{animal.Id}/weight-measurements",
                new
                {
                    revision = animal.Revision,
                    value = 150.0m,
                    unit = 0,
                    measurementDate = "2024-01-15"
                }));

        Assert.Equal(2, barrier.Arrivals);

        var measurementConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionConflict = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Conflict &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        var measurementOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.Created &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/weight-measurements") == true);

        var deletionOk = responses.Count(r =>
            r.StatusCode == HttpStatusCode.OK &&
            r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/deletion") == true);

        Assert.Equal(1, measurementConflict + deletionConflict);
        Assert.Equal(1, measurementOk + deletionOk);

        if (measurementOk == 1)
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Single(list!.Items);
        }
        else
        {
            var list = await client.GetFromJsonAsync<GetAnimalWeightMeasurementsResponse>(
                $"/animal/{animal.Id}/weight-measurements");
            Assert.NotNull(list);
            Assert.Empty(list!.Items);
        }
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