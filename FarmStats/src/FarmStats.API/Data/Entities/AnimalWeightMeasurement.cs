namespace FarmStats.API.Data.Entities;

public sealed class AnimalWeightMeasurement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AnimalId { get; set; }

    public decimal Value { get; set; }

    public WeightUnit Unit { get; set; }

    public DateOnly MeasurementDate { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public string? ActorUserId { get; set; }
}