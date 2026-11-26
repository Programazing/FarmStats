using FarmStats.API.Data.Entities;

namespace FarmStats.API.Features.Animal.Lifecycle;

public sealed class AnimalHistoryItem
{
    public Guid Id { get; init; }

    public AnimalHistoryEventType EventType { get; init; }

    public AnimalStatus? PreviousStatus { get; init; }

    public AnimalStatus? NewStatus { get; init; }

    public AnimalQuantityEventType? QuantityEventType { get; init; }

    public int? PreviousQuantity { get; init; }

    public int? NewQuantity { get; init; }

    public int? QuantityDelta { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    public AnimalDeletionReason? DeletionReason { get; init; }

    public string? Notes { get; init; }

    public string? ActorUserId { get; init; }
}

public sealed class AnimalHistoryResponse
{
    public Guid Revision { get; init; }

    public List<AnimalHistoryItem> Items { get; init; } = [];

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages =>
        (int)Math.Ceiling(TotalCount / (double)PageSize);
}