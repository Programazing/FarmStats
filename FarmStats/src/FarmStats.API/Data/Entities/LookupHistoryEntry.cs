namespace FarmStats.API.Data.Entities;

public enum LookupKind { Species = 1, Breed = 2, Area = 3 }
public enum LookupHistoryEventType { Deleted = 1, Restored = 2 }
public enum LookupDeletionReason
{
    DuplicateRecord = 1,
    CreatedByMistake = 2,
    NoLongerUsed = 3,
    TestData = 4,
    Other = 5
}

// Polymorphic audit reference: LookupKind + LookupId.
// There is intentionally no FK from LookupId to three separate tables.
public sealed class LookupHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LookupKind LookupKind { get; set; }
    public Guid LookupId { get; set; }
    public required string NameSnapshot { get; set; }
    public LookupHistoryEventType EventType { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public LookupDeletionReason? DeletionReason { get; set; }
    public string? Notes { get; set; }
    public string? ActorUserId { get; set; }
}
