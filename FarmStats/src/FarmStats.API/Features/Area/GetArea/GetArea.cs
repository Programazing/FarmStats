using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Area.GetArea;

public sealed class Endpoint(FarmStatsDbContext db)
    : EndpointWithoutRequest<List<GetAreasResponse>>
{
    public override void Configure()
    {
        Get("/area");
        Description(builder => builder.WithTags("Area"));

        // Local development only. Require authentication and farm ownership
        // filtering before exposing real farm areas.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Get farm areas";
            summary.Description = "Returns all areas for local development, ordered by name and ID. "
                + "Farm ownership filtering must be added before multi-farm deployment.";
            summary.Response<List<GetAreasResponse>>(
                StatusCodes.Status200OK, "The area lookup list; empty when no areas exist.");
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Add the authenticated farm ownership predicate before ordering.
        var areas = await db.Areas
            .OrderBy(area => area.Name)
            .ThenBy(area => area.Id)
            .Select(area => new GetAreasResponse
            {
                Revision = area.Revision,
                Id = area.Id,
                Name = area.Name
            })
            .ToListAsync(ct);

        await Send.ResponseAsync(areas, StatusCodes.Status200OK, ct);
    }
}

public sealed class GetAreasResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
