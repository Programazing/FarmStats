namespace FarmStats.API.Data.Entities;

public sealed class Species : ISoftDeletableLookup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
    public LookupDeletionReason? DeletionReason { get; set; }
    public string? DeletionNotes { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
}