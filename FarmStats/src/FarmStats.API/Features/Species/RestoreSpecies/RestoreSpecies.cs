using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.LookupLifecycle;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Species.RestoreSpecies;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<RestoreSpeciesRequest, LookupLifecycleResponse>
{
    public override void Configure()
    {
        Post("/species/{Id}/restore");
        Description(builder => builder.WithTags("Species"));
        AllowAnonymous(); // Development only: add authorization and farm scope.
        Summary(summary =>
        {
            summary.Description = "Supply the revision from the latest response; stale revisions return 409.";
            summary.Summary = "Restore a species";
            summary.Response<LookupLifecycleResponse>(200, "The resulting lookup state.");
            summary.Response(400, "Invalid ID, reason, or notes.");
            summary.Response(404, "Lookup not found.");
            summary.Response(409, "Retained references, invalid state, or concurrent modification.");
        });
    }

    public override async Task HandleAsync(RestoreSpeciesRequest req, CancellationToken ct)
    {
        // IgnoreQueryFilters in restore must be revisited when farm filters exist.
        var entity = await db.Species.IgnoreQueryFilters()
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


        LookupLifecycleSupport.Record(db, entity, LookupKind.Species,
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

public sealed class RestoreSpeciesRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }
    public string? Notes { get; set; }
}

public sealed class Validator : Validator<RestoreSpeciesRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id).NotEmpty();
        RuleFor(req => req.Notes).MaximumLength(2000);
    }
}
