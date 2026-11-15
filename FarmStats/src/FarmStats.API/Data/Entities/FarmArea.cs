namespace FarmStats.API.Data.Entities;

public sealed class FarmArea
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }
}