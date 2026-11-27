namespace FarmStats.API.Data.Entities;

public sealed class AnimalNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AnimalId { get; set; }

    public string Text { get; set; } = string.Empty;

    public DateOnly EffectiveDate { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public string? ActorUserId { get; set; }
}