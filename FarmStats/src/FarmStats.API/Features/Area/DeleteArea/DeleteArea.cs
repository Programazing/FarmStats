using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.LookupLifecycle;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Area.DeleteArea;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<DeleteAreaRequest, LookupLifecycleResponse>
{
    public override void Configure()
    {
        Post("/area/{Id}/deletion");
        Description(builder => builder.WithTags("Area"));
        AllowAnonymous(); // Development only: add authorization and farm scope.
        Summary(summary =>
        {
            summary.Description = "Supply the revision from the latest response; stale revisions return 409.";
            summary.Summary = "Soft-delete a area";
            summary.Response<LookupLifecycleResponse>(200, "The resulting lookup state.");
            summary.Response(400, "Invalid ID, reason, or notes.");
            summary.Response(404, "Lookup not found or already deleted.");
            summary.Response(409, "Retained references, invalid state, or concurrent modification.");
        });
    }

    public override async Task HandleAsync(DeleteAreaRequest req, CancellationToken ct)
    {
        // IgnoreQueryFilters in restore must be revisited when farm filters exist.
        var entity = await db.Areas
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

        if (await LookupLifecycleSupport.HasReferences(db, LookupKind.Area, entity.Id, ct))
        {
            await LookupLifecycleSupport.Conflict(HttpContext,
                "This lookup is referenced by retained records, including deleted records. "
                + "It cannot be deleted. No dependent records were changed.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var notes = LookupLifecycleSupport.Clean(req.Notes);
        LookupLifecycleSupport.Record(db, entity, LookupKind.Area,
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

public sealed class DeleteAreaRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }
    public LookupDeletionReason Reason { get; set; } = (LookupDeletionReason)(-1);
    public string? Notes { get; set; }
}

public sealed class Validator : Validator<DeleteAreaRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id).NotEmpty();
        RuleFor(req => req.Notes).MaximumLength(2000);
        RuleFor(req => req.Reason).IsInEnum()
            .WithMessage("A valid lookup deletion reason must be supplied.");
        RuleFor(req => req.Notes).NotEmpty()
            .When(req => req.Reason == LookupDeletionReason.Other)
            .WithMessage("Notes are required when the reason is Other.");
    }
}
