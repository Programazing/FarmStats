using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.LookupLifecycle;

public sealed class LookupLifecycleResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset? DeletedAtUtc { get; init; }
    public LookupDeletionReason? DeletionReason { get; init; }
    public string? DeletionNotes { get; init; }
}

public static class LookupLifecycleSupport
{
    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static LookupLifecycleResponse ToResponse(ISoftDeletableLookup lookup) => new()
    {
        Id = lookup.Id,
        Name = lookup.Name,
        DeletedAtUtc = lookup.DeletedAtUtc,
        DeletionReason = lookup.DeletionReason,
        DeletionNotes = lookup.DeletionNotes
    };

    public static async Task<bool> HasReferences(
        FarmStatsDbContext db, LookupKind kind, Guid id, CancellationToken ct)
    {
        // Retained history matters: deliberately include soft-deleted dependents.
        // Revisit IgnoreQueryFilters before adding farm/tenant query filters.
        return kind switch
        {
            LookupKind.Species =>
                await db.Breeds.IgnoreQueryFilters().AnyAsync(b => b.SpeciesId == id, ct)
                || await db.Animals.IgnoreQueryFilters().AnyAsync(a => a.SpeciesId == id, ct),
            LookupKind.Breed =>
                await db.Animals.IgnoreQueryFilters().AnyAsync(a => a.BreedId == id, ct),
            LookupKind.Area =>
                await db.Animals.IgnoreQueryFilters().AnyAsync(a => a.AreaId == id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static void Record(
        FarmStatsDbContext db, ISoftDeletableLookup lookup, LookupKind kind,
        LookupHistoryEventType eventType, LookupDeletionReason? reason,
        string? notes, DateTimeOffset now)
    {
        db.LookupHistory.Add(new LookupHistoryEntry
        {
            LookupKind = kind,
            LookupId = lookup.Id,
            NameSnapshot = lookup.Name,
            EventType = eventType,
            RecordedAtUtc = now,
            DeletionReason = reason,
            Notes = notes
        });
    }

    public static Task Conflict(HttpContext context, string detail) =>
        Results.Problem(statusCode: 409, title: "Lookup operation conflict", detail: detail)
            .ExecuteAsync(context);
}
