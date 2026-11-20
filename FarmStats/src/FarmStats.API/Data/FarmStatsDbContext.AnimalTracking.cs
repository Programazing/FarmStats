using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Data;

public sealed partial class FarmStatsDbContext
{
    private void ConfigureAnimalTracking(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Animal>().ToTable("Animals", table =>
            table.HasCheckConstraint("CK_Animals_TrackingType_Quantity",
                "(\"TrackingType\" = 0 AND \"Quantity\" >= 1) OR "
                + "(\"TrackingType\" = 1 AND \"Quantity\" = 1) OR "
                + "(\"TrackingType\" = 2 AND \"Quantity\" >= 0)"));
    }

    private void ValidateAnimalTracking()
    {
        foreach (var entry in ChangeTracker.Entries<Animal>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var animal = entry.Entity;
            var original = entry.Property(item => item.TrackingType).OriginalValue;
            if (entry.State == EntityState.Modified
                && original != AnimalTrackingType.Unspecified
                && original != animal.TrackingType)
            {
                throw new InvalidOperationException(
                    "Classified records cannot switch tracking type. "
                    + "Splitting or converting records requires a dedicated operation.");
            }

            var valid = animal.TrackingType switch
            {
                AnimalTrackingType.Unspecified => animal.Quantity >= 1,
                AnimalTrackingType.Individual => animal.Quantity == 1,
                AnimalTrackingType.Group => animal.Quantity >= 0,
                _ => false
            };

            if (!valid)
            {
                throw new InvalidOperationException(
                    "Individual quantity must be 1; group quantity cannot be negative; "
                    + "unclassified legacy quantity must remain positive.");
            }
        }
    }
}
