using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalQuantityEventDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync();
    }

    private FarmStatsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FarmStatsDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;

        return new FarmStatsDbContext(options);
    }

    [Fact]
    public async Task UpgradePreservesExistingAnimalAndHistory()
    {
        await using var db = CreateContext();

        var migrator = db.GetService<IMigrator>();

        // Build the database at the migration immediately before quantity events.
        var script = migrator.GenerateScript(
            "0",
            "20261120034000_AddAnimalTrackingType");

        await using var connection = new NpgsqlConnection(
            postgres.GetConnectionString());

        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(script, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        const string setup = """
            INSERT INTO "Species" ("Id", "Name", "Revision")
            VALUES (
                '20000000-0000-4000-8000-000000000001',
                'Quantity migration species',
                '20000000-0000-4000-8000-000000000002'
            );

            INSERT INTO "Animals"
                ("Id", "Name", "SpeciesId", "TrackingType",
                 "Quantity", "Sex", "Revision")
            VALUES (
                '20000000-0000-4000-8000-000000000003',
                'Existing batch',
                '20000000-0000-4000-8000-000000000001',
                2,
                25,
                0,
                '20000000-0000-4000-8000-000000000004'
            );

            INSERT INTO "AnimalHistory"
                ("Id", "AnimalId", "EventType", "NewStatus",
                 "EffectiveDate", "RecordedAtUtc", "Notes")
            VALUES (
                '20000000-0000-4000-8000-000000000005',
                '20000000-0000-4000-8000-000000000003',
                0,
                0,
                '2020-01-02',
                '2020-01-02T12:00:00Z',
                'Original history entry.'
            );
            """;

        await using (var command = new NpgsqlCommand(setup, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();

        var animal = await db.Animals.AsNoTracking().SingleAsync();

        Assert.Equal(
            Guid.Parse("20000000-0000-4000-8000-000000000003"),
            animal.Id);

        Assert.Equal("Existing batch", animal.Name);
        Assert.Equal(AnimalTrackingType.Group, animal.TrackingType);
        Assert.Equal(25, animal.Quantity);

        Assert.Equal(
            Guid.Parse("20000000-0000-4000-8000-000000000004"),
            animal.Revision);

        var history = await db.AnimalHistory
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(
            Guid.Parse("20000000-0000-4000-8000-000000000005"),
            history.Id);

        Assert.Equal(animal.Id, history.AnimalId);
        Assert.Equal(AnimalHistoryEventType.Created, history.EventType);
        Assert.Equal((AnimalStatus?)AnimalStatus.Active, history.NewStatus);
        Assert.Equal(
            (DateOnly?)new DateOnly(2020, 1, 2),
            history.EffectiveDate);

        Assert.Equal(
            new DateTimeOffset(2020, 1, 2, 12, 0, 0, TimeSpan.Zero),
            history.RecordedAtUtc);

        Assert.Equal("Original history entry.", history.Notes);
        Assert.Null(history.QuantityEventType);
        Assert.Null(history.PreviousQuantity);
        Assert.Null(history.NewQuantity);
        Assert.Null(history.QuantityDelta);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("missing reason")]
    [InlineData("missing previous quantity")]
    [InlineData("missing new quantity")]
    [InlineData("missing delta")]
    [InlineData("missing date")]
    [InlineData("negative previous quantity")]
    [InlineData("negative new quantity")]
    [InlineData("incorrect delta")]
    [InlineData("addition decreases count")]
    [InlineData("death increases count")]
    [InlineData("sale increases count")]
    [InlineData("unknown reason")]
    [InlineData("correction without notes")]
    [InlineData("correction with blank notes")]
    [InlineData("correction without change")]
    [InlineData("quantity fields on ordinary history")]
    public async Task ConstraintRejectsMalformedQuantityHistory(
        string scenario)
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var species = new Species
        {
            Name = "Quantity constraint species"
        };

        db.Species.Add(species);

        var animal = new Data.Entities.Animal
        {
            Name = "Quantity constraint batch",
            SpeciesId = species.Id,
            TrackingType = AnimalTrackingType.Group,
            Quantity = 10
        };

        db.Animals.Add(animal);
        await db.SaveChangesAsync();

        int eventType = (int)AnimalHistoryEventType.QuantityChanged;
        int? reason = (int)AnimalQuantityEventType.Addition;
        int? previousQuantity = 10;
        int? newQuantity = 11;
        int? delta = 1;
        DateOnly? effectiveDate = new DateOnly(2020, 1, 2);
        string? notes = null;

        switch (scenario)
        {
            case "missing reason":
                reason = null;
                break;

            case "missing previous quantity":
                previousQuantity = null;
                break;

            case "missing new quantity":
                newQuantity = null;
                break;

            case "missing delta":
                delta = null;
                break;

            case "missing date":
                effectiveDate = null;
                break;

            case "negative previous quantity":
                previousQuantity = -1;
                newQuantity = 0;
                break;

            case "negative new quantity":
                reason = (int)AnimalQuantityEventType.Death;
                previousQuantity = 0;
                newQuantity = -1;
                delta = -1;
                break;

            case "incorrect delta":
                delta = 2;
                break;

            case "addition decreases count":
                newQuantity = 9;
                delta = -1;
                break;

            case "death increases count":
                reason = (int)AnimalQuantityEventType.Death;
                break;

            case "sale increases count":
                reason = (int)AnimalQuantityEventType.Sale;
                break;

            case "unknown reason":
                reason = 99;
                break;

            case "correction without notes":
                reason = (int)AnimalQuantityEventType.Correction;
                break;

            case "correction with blank notes":
                reason = (int)AnimalQuantityEventType.Correction;
                notes = "   ";
                break;

            case "correction without change":
                reason = (int)AnimalQuantityEventType.Correction;
                newQuantity = previousQuantity;
                delta = 0;
                notes = "Count checked.";
                break;

            case "quantity fields on ordinary history":
                eventType = (int)AnimalHistoryEventType.StatusChanged;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var historyId = Guid.NewGuid();
        var recordedAtUtc = DateTimeOffset.UtcNow;

        // Bypass application validation to test PostgreSQL enforcement.
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AnimalHistory"
                    ("Id", "AnimalId", "EventType", "QuantityEventType",
                     "PreviousQuantity", "NewQuantity", "QuantityDelta",
                     "EffectiveDate", "RecordedAtUtc", "Notes")
                VALUES
                    ({historyId}, {animal.Id}, {eventType}, {reason},
                     {previousQuantity}, {newQuantity}, {delta},
                     {effectiveDate}, {recordedAtUtc}, {notes})
                """));

        Assert.Equal("23514", error.SqlState);
        Assert.Equal(
            "CK_AnimalHistory_QuantityEvent",
            error.ConstraintName);

        Assert.False(await db.AnimalHistory.AnyAsync(
            item => item.Id == historyId));

        var savedAnimal = await db.Animals
            .AsNoTracking()
            .SingleAsync(item => item.Id == animal.Id);

        Assert.Equal(10, savedAnimal.Quantity);
        Assert.Equal(animal.Revision, savedAnimal.Revision);
    }
}