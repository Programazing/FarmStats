using FarmStats.API.Data;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.UpdateSpecies;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<UpdateSpeciesRequest, UpdateSpeciesResponse>
{
    public override void Configure()
    {
        Put("/species/{Id}");
        Description(builder => builder.WithTags("Species"));

        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Description = "Supply the revision from the latest response; stale revisions return 409.";
            summary.Summary = "Update a species";
            summary.Response(409, "Concurrent modification; reload and retry.");
            summary.Response<UpdateSpeciesResponse>(200, "The saved species.");
            summary.Response(400, "Invalid input or lookup relationships.");
            summary.Response(404, "The record does not exist.");
        });
    }

    public override async Task HandleAsync(UpdateSpeciesRequest req, CancellationToken ct)
    {
        var entity = await db.Species
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

        var name = req.Name.Trim();
        var normalizedName = await LookupUniqueness.NormalizeAsync(db, name, ct);

        var duplicateExists = await db.Species.IgnoreQueryFilters()
            .AnyAsync(item => item.Id != req.Id
                    && EF.Property<string>(item, "NormalizedName") == normalizedName, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A species with this name already exists (including deleted records).");
        }

        ThrowIfAnyErrors();

        entity.Name = name;

        db.Entry(entity).Property(item => item.Revision).IsModified = true;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await FarmStats.API.Features.LookupLifecycle.LookupLifecycleSupport.Conflict(
                HttpContext, "The lookup changed during this request. Reload and retry.");
            return;
        }

        await Send.ResponseAsync(new UpdateSpeciesResponse
        {
            Revision = entity.Revision,
            Id = entity.Id,
            Name = entity.Name
        }, 200, ct);
    }
}

public sealed class UpdateSpeciesRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateSpeciesResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class UpdateSpeciesValidator : Validator<UpdateSpeciesRequest>
{
    public UpdateSpeciesValidator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Species ID must not be an empty GUID.");

        // Application-level limit. Keep aligned with the entity/database schema.
        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Species name is required.")
            .MaximumLength(100);
    }
}
