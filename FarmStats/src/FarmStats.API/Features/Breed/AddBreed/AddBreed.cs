using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Breed.AddBreed;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<AddBreedRequest, AddBreedResponse>
{
    public override void Configure()
    {
        Post("/breed");
        Description(builder => builder.WithTags("Breed"));

        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Add a breed";
            summary.Response<AddBreedResponse>(201, "The saved breed.");
            summary.Response(400, "Invalid input or lookup relationships.");
        });
    }

    public override async Task HandleAsync(AddBreedRequest req, CancellationToken ct)
    {
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
            .AnyAsync(item => item.Name.Trim().ToUpper() == normalizedName
                    && item.SpeciesId == req.SpeciesId, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A breed with this name already exists for the selected species.");
        }

        ThrowIfAnyErrors();

        // Target-typed construction uses the entity type from the DbSet.
        var entity = db.Breeds.Add(new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SpeciesId = req.SpeciesId
        }).Entity;

        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new AddBreedResponse
        {
            Id = entity.Id,
            Name = entity.Name,
            SpeciesId = entity.SpeciesId
        }, 201, ct);
    }
}

public sealed class AddBreedRequest
{
    public string Name { get; set; } = string.Empty;

    public Guid SpeciesId { get; set; }
}

public sealed class AddBreedResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid SpeciesId { get; init; }
}

public sealed class AddBreedValidator : Validator<AddBreedRequest>
{
    public AddBreedValidator()
    {
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