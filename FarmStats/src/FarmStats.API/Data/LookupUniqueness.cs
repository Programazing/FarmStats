using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FarmStats.API.Data;

public static class LookupUniqueness
{
    public const string SpeciesIndex = "UX_Species_NormalizedName";
    public const string BreedIndex = "UX_Breeds_SpeciesId_NormalizedName";
    public const string AreaIndex = "UX_Areas_NormalizedName";

    public static Task<string> NormalizeAsync(FarmStatsDbContext db, string name, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"SELECT upper(btrim({name})) AS \"Value\"").SingleAsync(ct);

    public static bool IsConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: SpeciesIndex or BreedIndex or AreaIndex
        };
}