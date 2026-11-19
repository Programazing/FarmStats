using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.RestoreAnimal;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<RestoreAnimalRequest, AnimalLifecycleResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/restore");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Restore a deleted animal record";
            summary.Description =
                "Restores visibility without changing lifecycle status. "
                + "Deletion details remain in history.";
            summary.Response<AnimalLifecycleResponse>(200, "The restored lifecycle state.");
            summary.Response(400, "Invalid ID or notes.");
            summary.Response(404, "Animal not found.");
            summary.Response(409, "Already restored or concurrently modified.");
        });
    }

    public override async Task HandleAsync(
        RestoreAnimalRequest req,
        CancellationToken ct)
    {
        // CURRENT MODEL: only the animal soft-delete filter is bypassed.
        // If tenant/farm filters are introduced, retain/reapply farm scope here
        // or use selectively disabled named filters supported by your EF version.
        var animal = await db.Animals
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(animal => animal.Id == req.Id, ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.DeletedAtUtc is null)
        {
            await Results.Problem(
                    statusCode: 409,
                    title: "The animal record is not deleted.")
                .ExecuteAsync(HttpContext);
            return;
        }

        db.AnimalHistory.Add(new AnimalHistoryEntry
        {
            AnimalId = animal.Id,
            EventType = AnimalHistoryEventType.Restored,
            PreviousStatus = animal.Status,
            NewStatus = animal.Status,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            DeletionReason = animal.DeletionReason,
            Notes = AnimalLifecycleSupport.Clean(req.Notes)
        });

        animal.DeletedAtUtc = null;
        animal.DeletionReason = null;
        animal.DeletionNotes = null;

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

public sealed class RestoreAnimalRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public string? Notes { get; set; }
}

public sealed class Validator : Validator<RestoreAnimalRequest>
{
    public Validator()
    {
        RuleFor(req => req.Id).NotEmpty();
        RuleFor(req => req.Notes).MaximumLength(2000);
    }
}
