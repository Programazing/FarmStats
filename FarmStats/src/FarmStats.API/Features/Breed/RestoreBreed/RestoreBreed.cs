using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.LookupLifecycle;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Breed.RestoreBreed;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<RestoreBreedRequest, LookupLifecycleResponse>
{
    public override void Configure()
    {
        Post("/breed/{Id}/restore");
        Description(builder => builder.WithTags("Breed"));
        AllowAnonymous(); // Development only: add authorization and farm scope.
        Summary(summary =>
        {
            summary.Description = "Supply the revision from the latest response; stale revisions return 409.";
            summary.Summary = "Restore a breed";
            summary.Response<LookupLifecycleResponse>(200, "The resulting lookup state.");
            summary.Response(400, "Invalid ID, reason, or notes.");
            summary.Response(404, "Lookup not found.");
            summary.Response(409, "Retained references, invalid state, or concurrent modification.");
        });
    }

    public override async Task HandleAsync(RestoreBreedRequest req, CancellationToken ct)
    {
        // IgnoreQueryFilters in restore must be revisited when farm filters exist.
        var entity = await db.Breeds.IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == req.Id, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (entity.Revision != req.Revision)
        {
            await FarmStats.API.Features.LookupLifecycle.LookupLifecycleSupport.Conflict(HttpContext, "The lookup has changed. Reload and retry.");
            return;
        }

        db.Entry(entity).Property(item => item.Revision).OriginalValue = req.Revision;

        if (entity.DeletedAtUtc is null)
        {
            await LookupLifecycleSupport.Conflict(HttpContext, "The lookup is not deleted.");
            return;
        }

        if (!await db.Species.AnyAsync(s => s.Id == entity.SpeciesId, ct))
        {
            await LookupLifecycleSupport.Conflict(HttpContext,
                "Restore the parent species before restoring this breed.");
            return;
        }

        LookupLifecycleSupport.Record(db, entity, LookupKind.Breed,
            LookupHistoryEventType.Restored, entity.DeletionReason,
            LookupLifecycleSupport.Clean(req.Notes), DateTimeOffset.UtcNow);
        entity.DeletedAtUtc = null;
        entity.DeletionReason = null;
        entity.DeletionNotes = null;

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

public sealed class RestoreBreedRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }
    public string? Notes { get; set; }
}

public sealed class Validator : Validator<RestoreBreedRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id).NotEmpty();
        RuleFor(req => req.Notes).MaximumLength(2000);
    }
}
