using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;
using AnimalEntity = FarmStats.API.Data.Entities.Animal;

namespace FarmStats.API.Features.Animal.AddAnimal;

public sealed class AddAnimal(FarmStatsDbContext db)
    : Endpoint<AddAnimalRequest, AddAnimalResponse>
{
    public override void Configure()
    {
        Post("/animal");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Add an animal";

            summary.Description =
                "Creates an individual (trackingType 1, quantity 1) or group "
                + "(trackingType 2, positive initial quantity). Omitted trackingType "
                + "defaults to Individual; quantity is never used to infer the type.";

            summary.Response<AddAnimalResponse>(
                StatusCodes.Status201Created,
                "The animal was created.");

            summary.Response(
                StatusCodes.Status400BadRequest,
                "Invalid input or lookup relationships.");
        });
    }

    public override async Task HandleAsync(
        AddAnimalRequest req,
        CancellationToken ct)
    {
        var speciesExists = await db.Species
            .AnyAsync(species => species.Id == req.SpeciesId, ct);

        if (!speciesExists)
        {
            AddError(
                request => request.SpeciesId,
                "The selected species does not exist.");
        }

        if (req.BreedId is Guid breedId)
        {
            var breedMatchesSpecies = await db.Breeds
                .AnyAsync(
                    breed => breed.Id == breedId
                        && breed.SpeciesId == req.SpeciesId,
                    ct);

            if (!breedMatchesSpecies)
            {
                AddError(
                    request => request.BreedId,
                    "The selected breed does not exist or does not "
                    + "belong to the selected species.");
            }
        }

        if (req.AreaId is Guid areaId)
        {
            var areaExists = await db.Areas
                .AnyAsync(area => area.Id == areaId, ct);

            if (!areaExists)
            {
                AddError(
                    request => request.AreaId,
                    "The selected farm area does not exist.");
            }
        }

        ThrowIfAnyErrors();

        var animal = new AnimalEntity
        {
            Name = req.Name,
            SpeciesId = req.SpeciesId,
            BreedId = req.BreedId,
            TrackingType = req.TrackingType,
            Quantity = req.Quantity,
            ReferenceId = req.ReferenceId,
            Sex = req.Sex,
            Color = req.Color,
            Birthdate = req.Birthdate,
            AreaId = req.AreaId
        };

        db.Animals.Add(animal);
        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new AddAnimalResponse
        {
            Revision = animal.Revision,
            Id = animal.Id,
            Name = animal.Name,
            SpeciesId = animal.SpeciesId,
            BreedId = animal.BreedId,
            TrackingType = animal.TrackingType,
            Quantity = animal.Quantity,
            ReferenceId = animal.ReferenceId,
            Sex = animal.Sex,
            Color = animal.Color,
            Birthdate = animal.Birthdate,
            AreaId = animal.AreaId,
            Status = animal.Status,
            StatusEffectiveDate = animal.StatusEffectiveDate,
            StatusNotes = animal.StatusNotes
        }, 201, ct);
    }
}

public sealed class AddAnimalRequest
{
    public string Name { get; set; } = "";

    public Guid SpeciesId { get; set; }

    public Guid? BreedId { get; set; }

    // Omitted type defaults to Individual for existing single-animal clients.
    public AnimalTrackingType TrackingType { get; set; } = AnimalTrackingType.Individual;

    public int Quantity { get; set; } = 1;

    public string? ReferenceId { get; set; }

    public AnimalSex Sex { get; set; } = AnimalSex.Unknown;

    public string? Color { get; set; }

    public DateOnly? Birthdate { get; set; }

    public Guid? AreaId { get; set; }
}

public sealed class AddAnimalResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid SpeciesId { get; set; }

    public Guid? BreedId { get; set; }

    public AnimalTrackingType TrackingType { get; init; }

    public int Quantity { get; set; }

    public string? ReferenceId { get; set; }

    public AnimalSex Sex { get; set; }

    public string? Color { get; set; }

    public DateOnly? Birthdate { get; set; }

    public Guid? AreaId { get; set; }

    public AnimalStatus Status { get; init; }

    public DateOnly? StatusEffectiveDate { get; init; }

    public string? StatusNotes { get; init; }
}

public sealed class AddAnimalValidator : Validator<AddAnimalRequest>
{
    public AddAnimalValidator()
    {
        RuleFor(req => req.Name)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(req => req.SpeciesId)
            .NotEmpty()
            .WithMessage("A species must be selected.");

        RuleFor(req => req.BreedId)
            .Must(id => !id.HasValue || id.Value != Guid.Empty)
            .WithMessage(
                "BreedId must be a non-empty ID when provided, or null.");

        RuleFor(req => req.TrackingType)
            .Must(type => type is AnimalTrackingType.Individual or AnimalTrackingType.Group)
            .WithMessage("TrackingType must be 1 (Individual) or 2 (Group).");

        RuleFor(req => req.Quantity)
            .GreaterThan(0)
            .WithMessage("Initial quantity must be at least 1.");

        RuleFor(req => req.Quantity)
            .Equal(1)
            .When(req => req.TrackingType == AnimalTrackingType.Individual)
            .WithMessage("An individually tracked animal must have quantity 1.");

        var allowedSexValues = string.Join(
            ", ",
            Enum.GetValues<AnimalSex>()
                .Select(value => $"{(int)value} ({value})"));

        RuleFor(req => req.Sex)
            .IsInEnum()
            .WithMessage($"Sex must be one of: {allowedSexValues}.");

        RuleFor(req => req.AreaId)
            .Must(id => !id.HasValue || id.Value != Guid.Empty)
            .WithMessage(
                "AreaId must be a non-empty ID when provided, or null.");
    }
}
