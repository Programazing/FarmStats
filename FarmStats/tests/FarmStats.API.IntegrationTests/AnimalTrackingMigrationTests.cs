using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class AnimalTrackingMigrationTests
{
    [Fact]
    public async Task UpgradeKeepsBothSingleAndMultipleLegacyRecordsUnclassified()
    {
        await using var postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<FarmStatsDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new FarmStatsDbContext(options);
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var migrator = db.GetService<IMigrator>();
        var sql = migrator.GenerateScript("0", "20261120010000_BackfillEmptyRevisions");
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        const string setup = """
            INSERT INTO "Species" ("Id", "Name", "Revision")
            VALUES ('10000000-0000-4000-8000-000000000001', 'Legacy tracking species', '10000000-0000-4000-8000-000000000002');
            INSERT INTO "Animals" ("Id", "Name", "SpeciesId", "Quantity", "Sex", "Revision", "DeletedAtUtc")
            VALUES
            ('10000000-0000-4000-8000-000000000003', 'Quantity one', '10000000-0000-4000-8000-000000000001', 1, 0, '10000000-0000-4000-8000-000000000005', NULL),
            ('10000000-0000-4000-8000-000000000004', 'Deleted batch', '10000000-0000-4000-8000-000000000001', 150, 0, '10000000-0000-4000-8000-000000000006', '2020-01-01T00:00:00Z');
            """;
        await using (var command = new NpgsqlCommand(setup, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();
        var rows = await db.Animals.IgnoreQueryFilters().OrderBy(a => a.Quantity).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(0, (int)row.TrackingType));
        Assert.Equal(1, rows[0].Quantity);
        Assert.Equal(150, rows[1].Quantity);
        Assert.NotNull(rows[1].DeletedAtUtc);
        Assert.Equal(Guid.Parse("10000000-0000-4000-8000-000000000005"), rows[0].Revision);
        Assert.Equal(Guid.Parse("10000000-0000-4000-8000-000000000006"), rows[1].Revision);
        Assert.Equal(0, await db.AnimalHistory.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
