using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Data;

public sealed class FarmStatsDbContext(
    DbContextOptions<FarmStatsDbContext> options)
    : DbContext(options)
{
    public DbSet<Animal> Animals => Set<Animal>();

    public DbSet<Species> Species => Set<Species>();

    public DbSet<Breed> Breeds => Set<Breed>();

    public DbSet<FarmArea> Areas => Set<FarmArea>();

    public DbSet<AnimalHistoryEntry> AnimalHistory =>
        Set<AnimalHistoryEntry>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareLifecycleChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareLifecycleChanges();

        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    private void PrepareLifecycleChanges()
    {
        ChangeTracker.DetectChanges();

        var animals = ChangeTracker.Entries<Animal>().ToList();

        if (animals.Any(entry => entry.State == EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Physical animal deletion is disabled. "
                + "Use the reasoned soft-delete endpoint.");
        }

        if (ChangeTracker.Entries<AnimalHistoryEntry>().Any(
                entry => entry.State is EntityState.Modified
                    or EntityState.Deleted))
        {
            throw new InvalidOperationException(
                "Animal history entries are append-only.");
        }

        foreach (var entry in animals)
        {
            if (entry.State == EntityState.Added)
            {
                // Avoid adding the initial event twice after a failed save.
                var alreadyTracked = ChangeTracker
                    .Entries<AnimalHistoryEntry>()
                    .Any(history =>
                        history.State == EntityState.Added
                        && history.Entity.AnimalId == entry.Entity.Id
                        && history.Entity.EventType
                            == AnimalHistoryEventType.Created);

                if (!alreadyTracked)
                {
                    AnimalHistory.Add(new AnimalHistoryEntry
                    {
                        AnimalId = entry.Entity.Id,
                        EventType = AnimalHistoryEventType.Created,
                        NewStatus = entry.Entity.Status,
                        EffectiveDate = entry.Entity.StatusEffectiveDate,
                        RecordedAtUtc = DateTimeOffset.UtcNow,
                        Notes = "Animal record created."
                    });
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.Revision = Guid.NewGuid();
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Animal>(entity =>
        {
            entity.HasQueryFilter(animal => animal.DeletedAtUtc == null);

            entity.Property(animal => animal.Revision)
                .IsConcurrencyToken();

            entity.Property(animal => animal.Status)
                .HasDefaultValue(AnimalStatus.Active);

            entity.Property(animal => animal.StatusNotes)
                .HasMaxLength(2000);

            entity.Property(animal => animal.DeletionNotes)
                .HasMaxLength(2000);

            entity.HasIndex(animal =>
                new { animal.DeletedAtUtc, animal.Status });
            entity.HasKey(animal => animal.Id);

            entity.Property(animal => animal.Name)
                .HasMaxLength(100)
                .IsRequired();

            entity.HasOne<Species>()
                .WithMany()
                .HasForeignKey(animal => animal.SpeciesId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Breed>()
                .WithMany()
                .HasForeignKey(animal => animal.BreedId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<FarmArea>()
                .WithMany()
                .HasForeignKey(animal => animal.AreaId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnimalHistoryEntry>(entity =>
        {
            entity.HasKey(history => history.Id);

            entity.Property(history => history.Notes)
                .HasMaxLength(2000);

            entity.Property(history => history.ActorUserId)
                .HasMaxLength(450);

            entity.HasIndex(history =>
                new { history.AnimalId, history.RecordedAtUtc });

            entity.HasOne<Animal>()
                .WithMany()
                .HasForeignKey(history => history.AnimalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Species>(entity =>
        {
            entity.HasKey(species => species.Id);

            entity.Property(species => species.Name)
                .IsRequired();
        });

        modelBuilder.Entity<Breed>(entity =>
        {
            entity.HasKey(breed => breed.Id);

            entity.Property(breed => breed.Name)
                .IsRequired();

            entity.HasOne<Species>()
                .WithMany()
                .HasForeignKey(breed => breed.SpeciesId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FarmArea>(entity =>
        {
            entity.HasKey(area => area.Id);

            entity.Property(area => area.Name)
                .IsRequired();
        });
    }
}
