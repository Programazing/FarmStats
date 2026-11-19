using FarmStats.API.Data.Entities;

namespace FarmStats.API.Features.Animal.Lifecycle;

public sealed class AnimalHistoryItem
{
    public Guid Id { get; init; }

    public AnimalHistoryEventType EventType { get; init; }

    public AnimalStatus? PreviousStatus { get; init; }

    public AnimalStatus? NewStatus { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    public AnimalDeletionReason? DeletionReason { get; init; }

    public string? Notes { get; init; }

    public string? ActorUserId { get; init; }
}

public sealed class AnimalHistoryResponse
{
    public List<AnimalHistoryItem> Items { get; init; } = [];

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages =>
        (int)Math.Ceiling(TotalCount / (double)PageSize);
}
