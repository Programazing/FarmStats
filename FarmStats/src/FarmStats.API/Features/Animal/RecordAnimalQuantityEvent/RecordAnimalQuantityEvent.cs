using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.RecordAnimalQuantityEvent;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<RecordAnimalQuantityEventRequest, RecordAnimalQuantityEventResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/quantity-events");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only; add farm-scoped authorization.

        Summary(summary =>
        {
            summary.Summary = "Record a group quantity event";
            summary.Description =
                "Requires an active, explicitly group-tracked animal and its current revision. "
                + "Addition increases the current count; Death and Sale decrease it. "
                + "Correction sets an absolute count and requires notes. "
                + "Backdated events affect the current count without reconstructing historical balances. "
                + "A zero count does not change lifecycle status.";
            summary.Response<RecordAnimalQuantityEventResponse>(
                200, "The recorded event and updated quantity.");
            summary.Response(400, "Invalid event or animal state.");
            summary.Response(404, "Animal not found or deleted.");
            summary.Response(409, "Concurrent modification; reload and retry.");
        });
    }

    public override async Task HandleAsync(
        RecordAnimalQuantityEventRequest req,
        CancellationToken ct)
    {
        var animal = await db.Animals
            .SingleOrDefaultAsync(item => item.Id == req.Id, ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.Revision != req.Revision)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        if (animal.TrackingType != AnimalTrackingType.Group)
        {
            AddError(request => request.Id,
                "Quantity events require an explicitly group-tracked animal.");
        }

        if (animal.Status != AnimalStatus.Active)
        {
            AddError(request => request.Id,
                "Quantity events require an active animal record.");
        }

        if (animal.Birthdate is DateOnly birthdate
            && req.EffectiveDate < birthdate)
        {
            AddError(request => request.EffectiveDate,
                "The effective date cannot precede the birthdate.");
        }

        if (animal.StatusEffectiveDate is DateOnly statusDate
            && req.EffectiveDate < statusDate)
        {
            AddError(request => request.EffectiveDate,
                "The effective date cannot precede the current status date.");
        }

        ThrowIfAnyErrors();

        var previousQuantity = animal.Quantity;

        // Calculate in long so an addition cannot overflow an int.
        long proposedQuantity = req.EventType switch
        {
            AnimalQuantityEventType.Addition =>
                (long)previousQuantity + req.Quantity!.Value,
            AnimalQuantityEventType.Death or AnimalQuantityEventType.Sale =>
                (long)previousQuantity - req.Quantity!.Value,
            AnimalQuantityEventType.Correction =>
                req.CorrectedQuantity!.Value,
            _ => throw new InvalidOperationException(
                "The quantity event type was not validated.")
        };

        if (proposedQuantity < 0)
        {
            AddError(request => request.Quantity,
                "The event cannot reduce the quantity below zero.");
        }
        else if (proposedQuantity > int.MaxValue)
        {
            AddError(request => request.Quantity,
                "The resulting quantity exceeds the supported maximum.");
        }

        if (req.EventType == AnimalQuantityEventType.Correction
            && proposedQuantity == previousQuantity)
        {
            AddError(request => request.CorrectedQuantity,
                "A correction must change the current quantity.");
        }

        ThrowIfAnyErrors();

        var newQuantity = (int)proposedQuantity;
        var history = new AnimalHistoryEntry
        {
            AnimalId = animal.Id,
            EventType = AnimalHistoryEventType.QuantityChanged,
            QuantityEventType = req.EventType,
            PreviousQuantity = previousQuantity,
            NewQuantity = newQuantity,
            QuantityDelta = newQuantity - previousQuantity,
            EffectiveDate = req.EffectiveDate,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Notes = AnimalLifecycleSupport.Clean(req.Notes)
        };

        db.Entry(animal)
            .Property(item => item.Revision)
            .OriginalValue = req.Revision;

        animal.Quantity = newQuantity;
        db.AnimalHistory.Add(history);

        try
        {
            // The quantity update and history insert share one save.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        await Send.ResponseAsync(new RecordAnimalQuantityEventResponse
        {
            Id = animal.Id,
            Revision = animal.Revision,
            EventId = history.Id,
            EventType = req.EventType,
            PreviousQuantity = previousQuantity,
            Quantity = animal.Quantity,
            QuantityDelta = history.QuantityDelta!.Value,
            EffectiveDate = req.EffectiveDate,
            RecordedAtUtc = history.RecordedAtUtc
        }, 200, ct);
    }
}

public sealed class RecordAnimalQuantityEventRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public AnimalQuantityEventType EventType { get; set; }
        = (AnimalQuantityEventType)(-1);

    public int? Quantity { get; set; }

    public int? CorrectedQuantity { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public string? Notes { get; set; }
}

public sealed class RecordAnimalQuantityEventResponse
{
    public Guid Id { get; init; }

    public Guid Revision { get; init; }

    public Guid EventId { get; init; }

    public AnimalQuantityEventType EventType { get; init; }

    public int PreviousQuantity { get; init; }

    public int Quantity { get; init; }

    public int QuantityDelta { get; init; }

    public DateOnly EffectiveDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed class Validator : Validator<RecordAnimalQuantityEventRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision)
            .NotEmpty()
            .WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id).NotEmpty();

        RuleFor(req => req.EventType)
            .IsInEnum()
            .WithMessage("A valid quantity event type must be supplied.");

        When(req => req.EventType is
            AnimalQuantityEventType.Addition
            or AnimalQuantityEventType.Death
            or AnimalQuantityEventType.Sale, () =>
        {
            RuleFor(req => req.Quantity)
                .NotNull()
                .WithMessage("Quantity must be supplied.")
                .GreaterThan(0)
                .WithMessage("Quantity must be positive.");

            RuleFor(req => req.CorrectedQuantity)
                .Null()
                .WithMessage(
                    "CorrectedQuantity is only valid for a correction.");
        });

        When(req => req.EventType == AnimalQuantityEventType.Correction, () =>
        {
            RuleFor(req => req.Quantity)
                .Null()
                .WithMessage(
                    "Supply CorrectedQuantity instead of Quantity for a correction.");

            RuleFor(req => req.CorrectedQuantity)
                .NotNull()
                .WithMessage("CorrectedQuantity must be supplied.")
                .GreaterThanOrEqualTo(0)
                .WithMessage("CorrectedQuantity cannot be negative.");

            RuleFor(req => req.Notes)
                .Must(notes => !string.IsNullOrWhiteSpace(notes))
                .WithMessage("A correction requires explanatory notes.");
        });

        RuleFor(req => req.EffectiveDate)
            .NotEmpty()
            .LessThanOrEqualTo(_ =>
                DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage(
                "Supply an effective date no later than today (UTC).");

        RuleFor(req => req.Notes)
            .MaximumLength(2000);
    }
}