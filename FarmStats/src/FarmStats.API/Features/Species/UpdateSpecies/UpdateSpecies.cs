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

        var name = req.Name.Trim();
        var normalizedName = name.ToUpperInvariant();

        var duplicateExists = await db.Species.IgnoreQueryFilters()
            .AnyAsync(item => item.Id != req.Id
                    && item.Name.Trim().ToUpper() == normalizedName, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A species with this name already exists (including deleted records).");
        }

        ThrowIfAnyErrors();

        entity.Name = name;

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
            Id = entity.Id,
            Name = entity.Name
        }, 200, ct);
    }
}

public sealed class UpdateSpeciesRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateSpeciesResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class UpdateSpeciesValidator : Validator<UpdateSpeciesRequest>
{
    public UpdateSpeciesValidator()
    {
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
