using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetAnimals;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<GetAnimalsRequest, GetAnimalsResponse>
{
    public override void Configure()
    {
        Get("/animals");

        // Local development only.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Get animals";

            summary.Description =
                "Returns a paginated list of animals, ordered by name and ID.";

            summary.Response<GetAnimalsResponse>(
                StatusCodes.Status200OK,
                "The requested page of animals.");

            summary.Response(
                StatusCodes.Status400BadRequest,
                "The pagination parameters are invalid.");
        });
    }

    public override async Task HandleAsync(
        GetAnimalsRequest req,
        CancellationToken ct)
    {
        var query = db.Animals.AsQueryable();

        // Apply the current user's farm restriction here
        // when authentication and farm ownership are implemented.

        var totalCount = await query.CountAsync(ct);

        var skip = (int)(((long)req.Page - 1) * req.PageSize);

        var items = await query
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Id)
            .Skip(skip)
            .Take(req.PageSize)
            .Select(a => new AnimalListItem
            {
                Id = a.Id,
                Name = a.Name,
                SpeciesId = a.SpeciesId,
                BreedId = a.BreedId,
                Quantity = a.Quantity,
                ReferenceId = a.ReferenceId,
                Sex = (int)a.Sex,
                Color = a.Color,
                Birthdate = a.Birthdate,
                AreaId = a.AreaId
            })
            .ToListAsync(ct);

        var response = new GetAnimalsResponse
        {
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalCount = totalCount
        };

        await Send.OkAsync(response);
    }
}

public sealed class GetAnimalsRequest
{
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 25;
}

public sealed class GetAnimalsResponse
{
    public List<AnimalListItem> Items { get; init; } = [];

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages =>
        (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class AnimalListItem
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public Guid SpeciesId { get; init; }

    public Guid? BreedId { get; init; }

    public int Quantity { get; init; }

    public string? ReferenceId { get; init; }

    public int Sex { get; init; }

    public string? Color { get; init; }

    public DateOnly? Birthdate { get; init; }

    public Guid? AreaId { get; init; }
}

public sealed class Validator : Validator<GetAnimalsRequest>
{
    public Validator()
    {
        RuleFor(x => x.Page)
            .Cascade(CascadeMode.Stop)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Page must be at least 1.")
            .Must((request, page) =>
                ((long)page - 1) * request.PageSize <= int.MaxValue)
            .WithMessage("The requested page is too large.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Page size must be between 1 and 100.");
    }
}