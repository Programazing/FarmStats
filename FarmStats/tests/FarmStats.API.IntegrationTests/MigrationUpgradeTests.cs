using FarmStats.API.Data;
using FarmStats.API.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class MigrationUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestMigrationAndSeedingWorkOnEmptyAndPopulatedInitialSchema(bool populated)
    {
        const string initial = "20261115195423_InitialCreate";
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

        if (populated)
        {
            var migrator = db.GetService<IMigrator>();
            await Execute(migrator.GenerateScript("0", initial));
            await Execute("""
                INSERT INTO "Species" ("Id", "Name") VALUES ('00000000-0000-0000-0000-000000000001', 'Historical species');
                INSERT INTO "Breeds" ("Id", "Name", "SpeciesId") VALUES ('00000000-0000-0000-0000-000000000002', 'Historical breed', '00000000-0000-0000-0000-000000000001');
                INSERT INTO "Areas" ("Id", "Name") VALUES ('00000000-0000-0000-0000-000000000003', 'Historical area');
                INSERT INTO "Animals" ("Id", "Name", "SpeciesId", "BreedId", "AreaId", "Quantity", "Sex", "ReferenceId", "Color", "Birthdate")
                VALUES ('00000000-0000-0000-0000-000000000004', 'Historical animal', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000003', 2, 1, 'ref', 'brown', '2020-01-02');
                """);
            await Execute(migrator.GenerateScript(initial));
        }
        else
        {
            await db.Database.MigrateAsync();
        }

        Assert.Equal(db.Database.GetMigrations().OrderBy(x => x), (await db.Database.GetAppliedMigrationsAsync()).OrderBy(x => x));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("0", await Scalar("SELECT count(*) FROM \"AnimalHistory\""));
        Assert.Equal("0", await Scalar("SELECT count(*) FROM \"LookupHistory\""));

        if (populated)
        {
            Assert.Equal("1", await Scalar("""
                SELECT count(*) FROM "Animals" a JOIN "Species" s ON s."Id" = a."SpeciesId"
                JOIN "Breeds" b ON b."Id" = a."BreedId" AND b."SpeciesId" = s."Id"
                JOIN "Areas" r ON r."Id" = a."AreaId"
                WHERE a."Name" = 'Historical animal' AND s."Name" = 'Historical species'
                  AND b."Name" = 'Historical breed' AND r."Name" = 'Historical area'
                  AND a."Quantity" = 2 AND a."Sex" = 1 AND a."ReferenceId" = 'ref'
                  AND a."TrackingType" = 0
                  AND a."Color" = 'brown' AND a."Birthdate" = '2020-01-02'
                  AND a."Status" = 0 AND a."StatusEffectiveDate" IS NULL AND a."StatusNotes" IS NULL
                  AND a."DeletedAtUtc" IS NULL AND a."DeletionReason" IS NULL AND a."DeletionNotes" IS NULL
                """));
            foreach (var table in new[] { "Animals", "Species", "Breeds", "Areas" })
            {
                Assert.Equal("1", await Scalar($"SELECT count(*) FROM \"{table}\" WHERE \"Revision\" <> '00000000-0000-0000-0000-000000000000' AND \"DeletedAtUtc\" IS NULL AND \"DeletionReason\" IS NULL AND \"DeletionNotes\" IS NULL"));
            }
        }
        else
        {
            foreach (var table in new[] { "Animals", "Species", "Breeds", "Areas" })
                Assert.Equal("0", await Scalar($"SELECT count(*) FROM \"{table}\""));
        }

        await ReferenceDataSeeder.SeedAsync(db, true);
        db.ChangeTracker.Clear();
        var area = await db.Areas.FirstAsync(a => a.Name != "Historical area");
        var breed = await db.Breeds.FirstAsync(b => b.Name != "Historical breed");
        var species = await db.Species.SingleAsync(s => s.Id == breed.SpeciesId);
        area.DeletedAtUtc = breed.DeletedAtUtc = species.DeletedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        const string catalogSnapshot = """
            SELECT jsonb_build_array(
              (SELECT jsonb_agg(to_jsonb(s) ORDER BY "Id") FROM "Species" s),
              (SELECT jsonb_agg(to_jsonb(b) ORDER BY "Id") FROM "Breeds" b),
              (SELECT jsonb_agg(to_jsonb(a) ORDER BY "Id") FROM "Areas" a))::text
            """;
        var before = await Scalar(catalogSnapshot);
        await ReferenceDataSeeder.SeedAsync(db, true);
        db.ChangeTracker.Clear();
        await ReferenceDataSeeder.SeedAsync(db, true);
        Assert.Equal(before, await Scalar(catalogSnapshot));
        Assert.NotNull((await db.Species.IgnoreQueryFilters().SingleAsync(s => s.Id == species.Id)).DeletedAtUtc);
        Assert.NotNull((await db.Breeds.IgnoreQueryFilters().SingleAsync(b => b.Id == breed.Id)).DeletedAtUtc);
        Assert.NotNull((await db.Areas.IgnoreQueryFilters().SingleAsync(a => a.Id == area.Id)).DeletedAtUtc);
        Assert.Equal("0", await Scalar("SELECT count(*) FROM \"AnimalHistory\""));
        Assert.Equal("0", await Scalar("SELECT count(*) FROM \"LookupHistory\""));
    }
}