namespace FarmStats.API.Data.Entities;

public sealed class Breed
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public Guid SpeciesId { get; set; }
}