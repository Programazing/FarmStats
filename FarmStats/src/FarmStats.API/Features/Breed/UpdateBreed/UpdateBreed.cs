using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Breed.UpdateBreed;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<UpdateBreedRequest, UpdateBreedResponse>
{
    public override void Configure()
    {
        Put("/breed/{Id}");
        Description(builder => builder.WithTags("Breed"));

        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Description = "Supply the revision from the latest response; stale revisions return 409.";
            summary.Summary = "Update a breed";
            summary.Response(409, "Concurrent modification; reload and retry.");
            summary.Response<UpdateBreedResponse>(200, "The saved breed.");
            summary.Response(400, "Invalid input or lookup relationships.");
            summary.Response(404, "The record does not exist.");
        });
    }

    public override async Task HandleAsync(UpdateBreedRequest req, CancellationToken ct)
    {
        var entity = await db.Breeds
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

        var speciesExists = await db.Species
            .AnyAsync(species => species.Id == req.SpeciesId, ct);

        if (!speciesExists)
        {
            AddError(request => request.SpeciesId,
                "The selected species does not exist.");
        }

        var duplicateExists = await db.Breeds.IgnoreQueryFilters()
            .AnyAsync(item => item.Id != req.Id
                    && EF.Property<string>(item, "NormalizedName") == normalizedName
                    && item.SpeciesId == req.SpeciesId, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A breed with this name already exists (including deleted records) for the selected species.");
        }

        // Reassigning an in-use breed would invalidate existing animals.
        if (entity.SpeciesId != req.SpeciesId)
        {
            var isInUse = await db.Animals.IgnoreQueryFilters()
                .AnyAsync(animal => animal.BreedId == entity.Id, ct);

            if (isInUse)
            {
                AddError(request => request.SpeciesId,
                    "This breed is used by animals and cannot be moved to another species.");
            }
        }

        ThrowIfAnyErrors();

        entity.Name = name;
        entity.SpeciesId = req.SpeciesId;

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

        await Send.ResponseAsync(new UpdateBreedResponse
        {
            Revision = entity.Revision,
            Id = entity.Id,
            Name = entity.Name,
            SpeciesId = entity.SpeciesId
        }, 200, ct);
    }
}

public sealed class UpdateBreedRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid SpeciesId { get; set; }
}

public sealed class UpdateBreedResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid SpeciesId { get; init; }
}

public sealed class UpdateBreedValidator : Validator<UpdateBreedRequest>
{
    public UpdateBreedValidator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Breed ID must not be an empty GUID.");

        // Application-level limit. Keep aligned with the entity/database schema.
        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Breed name is required.")
            .MaximumLength(100);

        RuleFor(req => req.SpeciesId)
            .NotEmpty()
            .WithMessage("A species must be selected.");
    }
}
