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
            summary.Summary = "Update a breed";
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

        var name = req.Name.Trim();
        var normalizedName = name.ToUpperInvariant();

        var speciesExists = await db.Species
            .AnyAsync(species => species.Id == req.SpeciesId, ct);

        if (!speciesExists)
        {
            AddError(request => request.SpeciesId,
                "The selected species does not exist.");
        }

        var duplicateExists = await db.Breeds
            .AnyAsync(item => item.Id != req.Id
                    && item.Name.Trim().ToUpper() == normalizedName
                    && item.SpeciesId == req.SpeciesId, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A breed with this name already exists for the selected species.");
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

        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new UpdateBreedResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            SpeciesId = entity.SpeciesId
        }, 200, ct);
    }
}

public sealed class UpdateBreedRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid SpeciesId { get; set; }
}

public sealed class UpdateBreedResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid SpeciesId { get; init; }
}

public sealed class UpdateBreedValidator : Validator<UpdateBreedRequest>
{
    public UpdateBreedValidator()
    {
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
