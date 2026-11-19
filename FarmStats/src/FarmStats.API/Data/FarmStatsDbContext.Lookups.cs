using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Data;

public sealed partial class FarmStatsDbContext
{
    public DbSet<LookupHistoryEntry> LookupHistory => Set<LookupHistoryEntry>();

    private void ConfigureLookupLifecycle(ModelBuilder modelBuilder)
    {
        ConfigureLookup<Species>(modelBuilder);
        ConfigureLookup<Breed>(modelBuilder);
        ConfigureLookup<FarmArea>(modelBuilder);

        modelBuilder.Entity<LookupHistoryEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.NameSnapshot).IsRequired();
            entity.Property(entry => entry.Notes).HasMaxLength(2000);
            entity.Property(entry => entry.ActorUserId).HasMaxLength(450);
            entity.HasIndex(entry => new
            {
                entry.LookupKind, entry.LookupId, entry.RecordedAtUtc
            });
        });
    }

    private static void ConfigureLookup<T>(ModelBuilder builder)
        where T : class, ISoftDeletableLookup
    {
        builder.Entity<T>().HasQueryFilter(entity => entity.DeletedAtUtc == null);
        builder.Entity<T>().Property(entity => entity.Revision).IsConcurrencyToken();
        builder.Entity<T>().Property(entity => entity.DeletionNotes).HasMaxLength(2000);
        builder.Entity<T>().HasIndex(entity => entity.DeletedAtUtc);
    }

    private void PrepareLookupLifecycleChanges()
    {
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is ISoftDeletableLookup lookup)
            {
                if (entry.State == EntityState.Deleted)
                    throw new InvalidOperationException(
                        "Physical lookup deletion is disabled. Use reasoned soft deletion.");

                if (entry.State == EntityState.Modified)
                    lookup.Revision = Guid.NewGuid();
            }

            if (entry.Entity is LookupHistoryEntry
                && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Lookup history is append-only.");
        }
    }
}
