using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.DeleteAnimal;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<DeleteAnimalRequest, AnimalLifecycleResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/deletion");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Soft-delete an animal record";
            summary.Description =
                "Supply the revision from the latest response; stale revisions return 409. " +
                "Requires a reason. Preserves lifecycle status and records an audit entry. "
                + "Use a status change, not deletion, to record a sale or death.";
            summary.Response<AnimalLifecycleResponse>(200, "The deleted record's lifecycle state.");
            summary.Response(400, "Invalid deletion reason or notes.");
            summary.Response(404, "Animal not found or already deleted.");
            summary.Response(409, "Concurrent modification; reload and retry.");
        });
    }

    public override async Task HandleAsync(
        DeleteAnimalRequest req,
        CancellationToken ct)
    {
        var animal = await db.Animals
            .SingleOrDefaultAsync(animal => animal.Id == req.Id, ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.Revision != req.Revision)
        {
            await FarmStats.API.Features.Animal.Lifecycle.AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        db.Entry(animal).Property(item => item.Revision).OriginalValue = req.Revision;

        var now = DateTimeOffset.UtcNow;
        var notes = AnimalLifecycleSupport.Clean(req.Notes);

        db.AnimalHistory.Add(new AnimalHistoryEntry
        {
            AnimalId = animal.Id,
            EventType = AnimalHistoryEventType.Deleted,
            PreviousStatus = animal.Status,
            NewStatus = animal.Status,
            RecordedAtUtc = now,
            DeletionReason = req.Reason,
            Notes = notes
        });

        animal.DeletedAtUtc = now;
        animal.DeletionReason = req.Reason;
        animal.DeletionNotes = notes;

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

public sealed class DeleteAnimalRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public AnimalDeletionReason Reason { get; set; } =
        (AnimalDeletionReason)(-1);

    public string? Notes { get; set; }
}

public sealed class Validator : Validator<DeleteAnimalRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id).NotEmpty();

        RuleFor(req => req.Reason)
            .IsInEnum()
            .WithMessage("A valid deletion reason must be supplied.");

        RuleFor(req => req.Notes).MaximumLength(2000);

        RuleFor(req => req.Notes)
            .NotEmpty()
            .When(req => req.Reason == AnimalDeletionReason.Other)
            .WithMessage("Notes are required when the reason is Other.");
    }
}
