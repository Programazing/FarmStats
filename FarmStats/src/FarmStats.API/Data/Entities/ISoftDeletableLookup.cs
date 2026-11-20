namespace FarmStats.API.Data.Entities;

public interface ISoftDeletableLookup
{
    Guid Id { get; set; }
    string Name { get; set; }
    DateTimeOffset? DeletedAtUtc { get; set; }
    LookupDeletionReason? DeletionReason { get; set; }
    string? DeletionNotes { get; set; }
    Guid Revision { get; set; }
}
