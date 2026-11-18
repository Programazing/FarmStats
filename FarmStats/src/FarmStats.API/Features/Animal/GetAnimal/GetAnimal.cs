using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetAnimal;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<GetAnimalRequest, GetAnimalResponse>
{
    public override void Configure()
    {
        Get("/animal/{Id}");

        // Local development only.
        // Replace with your authorization policy when authentication is added.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Get an animal by ID";

            summary.Description =
                "Returns the details of a single animal.";

            summary.Response<GetAnimalResponse>(
                StatusCodes.Status200OK,
                "The animal was found.");

            summary.Response(
                StatusCodes.Status404NotFound,
                "The animal does not exist.");
        });
    }

    public override async Task HandleAsync(
        GetAnimalRequest req,
        CancellationToken ct)
    {
        var animal = await db.Animals
            .AsNoTracking()
            .Where(a => a.Id == req.Id)
            .Select(a => new GetAnimalResponse
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
            .SingleOrDefaultAsync(ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        await Send.OkAsync(animal);
    }
}

public sealed class GetAnimalRequest
{
    public Guid Id { get; set; }
}

public sealed class GetAnimalResponse
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

public sealed class Validator : Validator<GetAnimalRequest>
{
    public Validator()
    {
        RuleFor(x => x.Id)
            .NotEmpty()
            .WithMessage("Animal ID must not be an empty GUID.");
    }
}