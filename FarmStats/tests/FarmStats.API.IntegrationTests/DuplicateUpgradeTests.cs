using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FarmStats.API.IntegrationTests;

public sealed class DuplicateUpgradeTests
{
    [Fact]
    public async Task GeneratedUpgradeFailsWithoutChangingDataUntilDuplicatesAreResolved()
    {
        const string historical = "20261119234126_AddLookupSoftDeletionAndHistory";
        const string uniqueness = "20261120004736_AddLookupNameUniqueness";
        await using var postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<FarmStatsDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new FarmStatsDbContext(options);
        var migrator = db.GetService<IMigrator>();
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

        await Execute(migrator.GenerateScript("0", historical));
        await Execute("""
            INSERT INTO "Species" ("Id", "Name", "DeletedAtUtc") VALUES
              ('00000000-0000-0000-0000-000000000001', 'Cow', NULL),
              ('00000000-0000-0000-0000-000000000002', ' cow ', '2020-01-01Z'),
              ('00000000-0000-0000-0000-000000000003', 'Sheep', NULL);
            INSERT INTO "Breeds" ("Id", "SpeciesId", "Name", "DeletedAtUtc") VALUES
              ('10000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'Mixed', NULL),
              ('10000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', ' mixed ', '2020-01-01Z'),
              ('10000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000003', 'mixed', NULL);
            INSERT INTO "Areas" ("Id", "Name", "DeletedAtUtc") VALUES
              ('20000000-0000-0000-0000-000000000001', 'Field', NULL),
              ('20000000-0000-0000-0000-000000000002', ' field ', '2020-01-01Z');
            """);

        const string speciesPreflight = "SELECT count(*) FROM (SELECT upper(btrim(\"Name\")) FROM \"Species\" GROUP BY 1 HAVING count(*) > 1) d";
        const string breedsPreflight = "SELECT count(*) FROM (SELECT \"SpeciesId\", upper(btrim(\"Name\")) FROM \"Breeds\" GROUP BY 1, 2 HAVING count(*) > 1) d";
        const string areasPreflight = "SELECT count(*) FROM (SELECT upper(btrim(\"Name\")) FROM \"Areas\" GROUP BY 1 HAVING count(*) > 1) d";
        const string snapshot = """
            SELECT jsonb_build_array(
              (SELECT jsonb_agg(to_jsonb(s) ORDER BY "Id") FROM "Species" s),
              (SELECT jsonb_agg(to_jsonb(b) ORDER BY "Id") FROM "Breeds" b),
              (SELECT jsonb_agg(to_jsonb(a) ORDER BY "Id") FROM "Areas" a),
              (SELECT jsonb_agg(to_jsonb(h) ORDER BY "MigrationId") FROM "__EFMigrationsHistory" h))::text
            """;
        var upgrade = migrator.GenerateScript(historical, uniqueness);
        Assert.Equal("1", await Scalar(speciesPreflight));
        Assert.Equal("1", await Scalar(breedsPreflight));
        Assert.Equal("1", await Scalar(areasPreflight));

        async Task FailWithoutChanges(string index)
        {
            var before = await Scalar(snapshot);
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(upgrade));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
            Assert.Equal(index, error.ConstraintName);
            await Execute("ROLLBACK");
            Assert.Equal(before, await Scalar(snapshot));
        }

        await FailWithoutChanges("UX_Species_NormalizedName");
        await Execute("UPDATE \"Species\" SET \"Name\" = 'Cow archived' WHERE \"Id\" = '00000000-0000-0000-0000-000000000002'");
        Assert.Equal("0", await Scalar(speciesPreflight));
        await FailWithoutChanges("UX_Breeds_SpeciesId_NormalizedName");
        await Execute("UPDATE \"Breeds\" SET \"Name\" = 'Mixed archived' WHERE \"Id\" = '10000000-0000-0000-0000-000000000002'");
        Assert.Equal("0", await Scalar(breedsPreflight));
        await FailWithoutChanges("UX_Areas_NormalizedName");
        await Execute("UPDATE \"Areas\" SET \"Name\" = 'Field archived' WHERE \"Id\" = '20000000-0000-0000-0000-000000000002'");
        Assert.Equal("0", await Scalar(areasPreflight));
        await Execute(upgrade);
        Assert.Equal("1", await Scalar($"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{uniqueness}'"));
        Assert.Equal("3", await Scalar("SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname IN ('UX_Species_NormalizedName', 'UX_Breeds_SpeciesId_NormalizedName', 'UX_Areas_NormalizedName')"));
        Assert.Equal("8", await Scalar("SELECT (SELECT count(*) FROM \"Species\") + (SELECT count(*) FROM \"Breeds\") + (SELECT count(*) FROM \"Areas\")"));
        await Execute(migrator.GenerateScript(uniqueness));
        Assert.Equal("1", await Scalar("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261120010000_BackfillEmptyRevisions'"));
    }
}