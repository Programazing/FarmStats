using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.LookupLifecycle;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Breed.DeleteBreed;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<DeleteBreedRequest, LookupLifecycleResponse>
{
    public override void Configure()
    {
        Post("/breed/{Id}/deletion");
        Description(builder => builder.WithTags("Breed"));
        AllowAnonymous(); // Development only: add authorization and farm scope.
        Summary(summary =>
        {
            summary.Summary = "Soft-delete a breed";
            summary.Response<LookupLifecycleResponse>(200, "The resulting lookup state.");
            summary.Response(400, "Invalid ID, reason, or notes.");
            summary.Response(404, "Lookup not found or already deleted.");
            summary.Response(409, "Retained references, invalid state, or concurrent modification.");
        });
    }

    public override async Task HandleAsync(DeleteBreedRequest req, CancellationToken ct)
    {
        // IgnoreQueryFilters in restore must be revisited when farm filters exist.
        var entity = await db.Breeds
            .SingleOrDefaultAsync(item => item.Id == req.Id, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (await LookupLifecycleSupport.HasReferences(db, LookupKind.Breed, entity.Id, ct))
        {
            await LookupLifecycleSupport.Conflict(HttpContext,
                "This lookup is referenced by retained records, including deleted records. "
                + "It cannot be deleted. No dependent records were changed.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var notes = LookupLifecycleSupport.Clean(req.Notes);
        LookupLifecycleSupport.Record(db, entity, LookupKind.Breed,
            LookupHistoryEventType.Deleted, req.Reason, notes, now);
        entity.DeletedAtUtc = now;
        entity.DeletionReason = req.Reason;
        entity.DeletionNotes = notes;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await LookupLifecycleSupport.Conflict(HttpContext,
                "The lookup changed during this request. Reload and retry.");
            return;
        }

        await Send.ResponseAsync(LookupLifecycleSupport.ToResponse(entity), 200, ct);
    }
}

public sealed class DeleteBreedRequest
{
    [RouteParam]
    public Guid Id { get; set; }
    public LookupDeletionReason Reason { get; set; } = (LookupDeletionReason)(-1);
    public string? Notes { get; set; }
}

public sealed class Validator : Validator<DeleteBreedRequest>
{
    public Validator()
    {
        RuleFor(req => req.Id).NotEmpty();
        RuleFor(req => req.Notes).MaximumLength(2000);
        RuleFor(req => req.Reason).IsInEnum()
            .WithMessage("A valid lookup deletion reason must be supplied.");
        RuleFor(req => req.Notes).NotEmpty()
            .When(req => req.Reason == LookupDeletionReason.Other)
            .WithMessage("Notes are required when the reason is Other.");
    }
}
