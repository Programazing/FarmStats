using FarmStats.API.Data.Entities;

namespace FarmStats.API.Features.Animal.Lifecycle;

public sealed class AnimalWeightMeasurementItem
{
    public Guid Id { get; init; }

    public decimal Value { get; init; }

    public WeightUnit Unit { get; init; }

    public DateOnly MeasurementDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    public string? ActorUserId { get; init; }
}

public sealed class LatestWeightSummary
{
    public Guid MeasurementId { get; init; }

    public decimal Value { get; init; }

    public WeightUnit Unit { get; init; }

    public DateOnly MeasurementDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed class CreateAnimalWeightMeasurementResponse
{
    public Guid Id { get; init; }

    public Guid Revision { get; init; }

    public Guid MeasurementId { get; init; }

    public decimal Value { get; init; }

    public WeightUnit Unit { get; init; }

    public DateOnly MeasurementDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed class GetAnimalWeightMeasurementsResponse
{
    public Guid Revision { get; init; }

    public List<AnimalWeightMeasurementItem> Items { get; init; } = new();

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages { get; init; }

    public bool HasPreviousPage { get; init; }

    public bool HasNextPage { get; init; }

    public LatestWeightSummary? LatestWeight { get; init; }
}