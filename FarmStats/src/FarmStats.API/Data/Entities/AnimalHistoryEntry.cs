namespace FarmStats.API.Data.Entities;

public sealed class AnimalHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AnimalId { get; set; }

    public AnimalHistoryEventType EventType { get; set; }

    public AnimalStatus? PreviousStatus { get; set; }

    public AnimalStatus? NewStatus { get; set; }

    public DateOnly? EffectiveDate { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public AnimalDeletionReason? DeletionReason { get; set; }

    public string? Notes { get; set; }

    // Reserved for authenticated, server-derived identity.
    // Never populate this from a request-body user ID.
    public string? ActorUserId { get; set; }
}
