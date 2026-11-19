using FarmStats.API.Data;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetSpecies;

public sealed class Endpoint(FarmStatsDbContext db)
    : EndpointWithoutRequest<List<GetSpeciesResponse>>
{
    public override void Configure()
    {
        Get("/species");
        // Local development only. Revisit access when authentication is added.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Get species";
            summary.Description = "Returns all species, ordered by name and ID.";
            summary.Response<List<GetSpeciesResponse>>(
                StatusCodes.Status200OK, "The species lookup list; empty when no species exist.");
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var species = await db.Species
            .OrderBy(species => species.Name)
            .ThenBy(species => species.Id)
            .Select(species => new GetSpeciesResponse
            {
                Id = species.Id,
                Name = species.Name
            })
            .ToListAsync(ct);

        await Send.ResponseAsync(species, StatusCodes.Status200OK, ct);
    }
}

public sealed class GetSpeciesResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
