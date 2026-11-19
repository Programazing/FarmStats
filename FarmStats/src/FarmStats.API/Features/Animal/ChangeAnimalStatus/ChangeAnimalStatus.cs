using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.ChangeAnimalStatus;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<ChangeAnimalStatusRequest, AnimalLifecycleResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/status-changes");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Change animal lifecycle status";
            summary.Description =
                "Applies to the entire animal record, including its full quantity. "
                + "Adds a history entry and saves the new current status atomically.";
            summary.Response<AnimalLifecycleResponse>(200, "The updated lifecycle state.");
            summary.Response(400, "Invalid status, effective date, or notes.");
            summary.Response(404, "Animal not found or deleted.");
            summary.Response(409, "Concurrent modification; reload and retry.");
        });
    }

    public override async Task HandleAsync(
        ChangeAnimalStatusRequest req,
        CancellationToken ct)
    {
        var animal = await db.Animals
            .SingleOrDefaultAsync(animal => animal.Id == req.Id, ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.Status == req.Status)
        {
            AddError(request => request.Status,
                "The animal already has this status.");
        }

        if (animal.Birthdate is DateOnly birthdate
            && req.EffectiveDate < birthdate)
        {
            AddError(request => request.EffectiveDate,
                "The effective date cannot precede the birthdate.");
        }

        if (animal.StatusEffectiveDate is DateOnly previousDate
            && req.EffectiveDate < previousDate)
        {
            AddError(request => request.EffectiveDate,
                "The effective date cannot precede the current status date. "
                + "Historical corrections require a separate correction workflow.");
        }

        ThrowIfAnyErrors();

        var notes = AnimalLifecycleSupport.Clean(req.Notes);

        db.AnimalHistory.Add(new AnimalHistoryEntry
        {
            AnimalId = animal.Id,
            EventType = AnimalHistoryEventType.StatusChanged,
            PreviousStatus = animal.Status,
            NewStatus = req.Status,
            EffectiveDate = req.EffectiveDate,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Notes = notes
        });

        animal.Status = req.Status;
        animal.StatusEffectiveDate = req.EffectiveDate;
        animal.StatusNotes = notes;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        await Send.ResponseAsync(animal.ToLifecycleResponse(), 200, ct);
    }
}

public sealed class ChangeAnimalStatusRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public AnimalStatus Status { get; set; } = (AnimalStatus)(-1);

    public DateOnly EffectiveDate { get; set; }

    public string? Notes { get; set; }
}

public sealed class Validator : Validator<ChangeAnimalStatusRequest>
{
    public Validator()
    {
        RuleFor(req => req.Id).NotEmpty();

        RuleFor(req => req.Status)
            .IsInEnum()
            .WithMessage("A valid lifecycle status must be supplied.");

        RuleFor(req => req.EffectiveDate)
            .NotEmpty()
            .LessThanOrEqualTo(_ =>
                DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Supply an effective date no later than today (UTC).");

        RuleFor(req => req.Notes)
            .MaximumLength(2000);
    }
}
