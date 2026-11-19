using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Breed.GetBreed;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<GetBreedsRequest, List<GetBreedsResponse>>
{
    public override void Configure()
    {
        Get("/species/{SpeciesId}/breed");
        // Local development only. Revisit access when authentication is added.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Get breeds for a species";
            summary.Description = "Returns breeds for an existing species, ordered by name and ID. "
                + "An existing species with no breeds returns an empty array.";
            summary.Response<List<GetBreedsResponse>>(
                StatusCodes.Status200OK, "The breed lookup list for the selected species.");
            summary.Response(StatusCodes.Status400BadRequest, "The species ID is invalid.");
            summary.Response(StatusCodes.Status404NotFound, "The species does not exist.");
        });
    }

    public override async Task HandleAsync(GetBreedsRequest req, CancellationToken ct)
    {
        var speciesExists = await db.Species
            .AnyAsync(species => species.Id == req.SpeciesId, ct);

        if (!speciesExists)
        {
            await Send.NotFoundAsync();
            return;
        }

        var breeds = await db.Breeds
            .Where(breed => breed.SpeciesId == req.SpeciesId)
            .OrderBy(breed => breed.Name)
            .ThenBy(breed => breed.Id)
            .Select(breed => new GetBreedsResponse
            {
                Id = breed.Id,
                Name = breed.Name,
                SpeciesId = breed.SpeciesId
            })
            .ToListAsync(ct);

        await Send.ResponseAsync(breeds, StatusCodes.Status200OK, ct);
    }
}

public sealed class GetBreedsRequest
{
    [RouteParam]
    public Guid SpeciesId { get; set; }
}

public sealed class GetBreedsResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid SpeciesId { get; init; }
}

public sealed class GetBreedsValidator : Validator<GetBreedsRequest>
{
    public GetBreedsValidator()
    {
        RuleFor(req => req.SpeciesId)
            .NotEmpty()
            .WithMessage("Species ID must not be an empty GUID.");
    }
}
