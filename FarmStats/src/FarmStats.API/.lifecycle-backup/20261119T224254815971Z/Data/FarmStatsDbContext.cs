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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Animal>(entity =>
        {
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