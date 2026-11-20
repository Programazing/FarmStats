using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.Lifecycle;

public sealed class AnimalLifecycleResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }

    public AnimalTrackingType TrackingType { get; init; }

    public int Quantity { get; init; }

    public AnimalStatus Status { get; init; }

    public DateOnly? StatusEffectiveDate { get; init; }

    public string? StatusNotes { get; init; }

    public DateTimeOffset? DeletedAtUtc { get; init; }

    public AnimalDeletionReason? DeletionReason { get; init; }

    public string? DeletionNotes { get; init; }
}

public static class AnimalLifecycleSupport
{
    public static AnimalLifecycleResponse ToLifecycleResponse(
        this Data.Entities.Animal animal) => new()
        {
            Revision = animal.Revision,
            Id = animal.Id,
            TrackingType = animal.TrackingType,
            Quantity = animal.Quantity,
            Status = animal.Status,
            StatusEffectiveDate = animal.StatusEffectiveDate,
            StatusNotes = animal.StatusNotes,
            DeletedAtUtc = animal.DeletedAtUtc,
            DeletionReason = animal.DeletionReason,
            DeletionNotes = animal.DeletionNotes
        };

    public static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static Task WriteConcurrencyProblem(HttpContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The animal changed during this request.",
            detail: "Reload the animal and retry the operation.")
        .ExecuteAsync(context);
}
