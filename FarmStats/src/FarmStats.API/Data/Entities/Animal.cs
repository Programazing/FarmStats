namespace FarmStats.API.Data.Entities;

public sealed class Animal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public Guid SpeciesId { get; set; }

    public Guid? BreedId { get; set; }

    public int Quantity { get; set; } = 1;

    public string? ReferenceId { get; set; }

    public AnimalSex Sex { get; set; } = AnimalSex.Unknown;

    public string? Color { get; set; }

    public DateOnly? Birthdate { get; set; }

    public Guid? AreaId { get; set; }
}