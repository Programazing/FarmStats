using FarmStats.API.Data;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.AddSpecies;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<AddSpeciesRequest, AddSpeciesResponse>
{
    public override void Configure()
    {
        Post("/species");
        Description(builder => builder.WithTags("Species"));

        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Add a species";
            summary.Response<AddSpeciesResponse>(201, "The saved species.");
            summary.Response(400, "Invalid input or lookup relationships.");
        });
    }

    public override async Task HandleAsync(AddSpeciesRequest req, CancellationToken ct)
    {
        var name = req.Name.Trim();
        var normalizedName = await LookupUniqueness.NormalizeAsync(db, name, ct);

        var duplicateExists = await db.Species.IgnoreQueryFilters()
            .AnyAsync(item => EF.Property<string>(item, "NormalizedName") == normalizedName, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A species with this name already exists (including deleted records).");
        }

        ThrowIfAnyErrors();

        // Target-typed construction uses the entity type from the DbSet.
        var entity = db.Species.Add(new()
        {
            Id = Guid.NewGuid(),
            Name = name
        }).Entity;

        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new AddSpeciesResponse
        {
            Revision = entity.Revision,
            Id = entity.Id,
            Name = entity.Name
        }, 201, ct);
    }
}

public sealed class AddSpeciesRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class AddSpeciesResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class AddSpeciesValidator : Validator<AddSpeciesRequest>
{
    public AddSpeciesValidator()
    {
        // Application-level limit. Keep aligned with the entity/database schema.
        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Species name is required.")
            .MaximumLength(100);
    }
}
