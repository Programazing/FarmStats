using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.UpdateAnimal;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<UpdateAnimalRequest, UpdateAnimalResponse>
{
    public override void Configure()
    {
        Put("/animal/{Id}");
        Description(builder => builder.WithTags("Animal"));

        // Local development only. Add authentication and farm-scoped
        // queries before exposing this endpoint to real users.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Update an animal";

            summary.Response(
                StatusCodes.Status409Conflict,
                "Concurrent modification; reload and retry.");

            summary.Description =
                "Supply the revision from the latest response; stale revisions return 409. " +
                "Replaces editable fields; omitted/null optional detail fields are cleared. "
                + "Omitted/null trackingType preserves the type. Unclassified legacy records "
                + "can be classified once; classified records cannot change type. Individual "
                + "quantity must be 1; group quantity may be 0. Does not create missing animals."
                + "quantity must be 1; group quantity may be 0. Group quantity changes require "
                + "the quantity-events endpoint. Legacy classification as a group must preserve "
                + "the current quantity. Does not create missing animals.";

            summary.Response<UpdateAnimalResponse>(
                StatusCodes.Status200OK,
                "The updated animal.");

            summary.Response(
                StatusCodes.Status400BadRequest,
                "Invalid input or lookup relationships.");

            summary.Response(
                StatusCodes.Status404NotFound,
                "The animal does not exist.");
        });
    }

    public override async Task HandleAsync(
        UpdateAnimalRequest req,
        CancellationToken ct)
    {
        // Add the authenticated farm ownership condition here
        // when farm ownership is implemented.
        var animal = await db.Animals
            .SingleOrDefaultAsync(
                animal => animal.Id == req.Id,
                ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.Revision != req.Revision)
        {
            await FarmStats.API.Features.Animal.Lifecycle.AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        db.Entry(animal).Property(item => item.Revision).OriginalValue = req.Revision;

        var trackingType = req.TrackingType ?? animal.TrackingType;
        if (req.Quantity is int requestedQuantity
            && requestedQuantity != animal.Quantity
            && trackingType == AnimalTrackingType.Group)
        {
            AddError(request => request.Quantity,
                "Group quantity changes require the quantity-events endpoint. "
                + "When classifying a legacy record as a group, preserve its current quantity.");
        }

        if (animal.TrackingType != AnimalTrackingType.Unspecified
            && trackingType != animal.TrackingType)
        {
            AddError(request => request.TrackingType,
                "An individual/group conversion requires a dedicated operation.");
        }

        if (req.Quantity is int quantity)
        {
            if (trackingType == AnimalTrackingType.Individual && quantity != 1)
            {
                AddError(request => request.Quantity,
                    "An individually tracked animal must have quantity 1.");
            }
            else if (trackingType == AnimalTrackingType.Unspecified && quantity < 1)
            {
                AddError(request => request.Quantity,
                    "Classify the legacy record as a group before setting quantity to zero.");
            }
        }

        var speciesExists = await db.Species
            .AnyAsync(
                species => species.Id == req.SpeciesId,
                ct);

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
            // Include farm ownership in this check when available.
            var areaExists = await db.Areas
                .AnyAsync(
                    area => area.Id == areaId,
                    ct);

            if (!areaExists)
            {
                AddError(
                    request => request.AreaId,
                    "The selected farm area does not exist.");
            }
        }

        // Do not modify the tracked entity until all checks pass.
        ThrowIfAnyErrors();

        animal.Name = req.Name.Trim();
        animal.SpeciesId = req.SpeciesId;
        animal.BreedId = req.BreedId;
        animal.TrackingType = trackingType;
        animal.Quantity = req.Quantity!.Value;
        animal.ReferenceId = CleanOptionalText(req.ReferenceId);
        animal.Sex = req.Sex;
        animal.Color = CleanOptionalText(req.Color);
        animal.Birthdate = req.Birthdate;
        animal.AreaId = req.AreaId;

        db.Entry(animal).Property(item => item.Revision).IsModified = true;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await FarmStats.API.Features.Animal.Lifecycle
                .AnimalLifecycleSupport
                .WriteConcurrencyProblem(HttpContext);
            return;
        }

        await Send.ResponseAsync(new UpdateAnimalResponse
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
        }, StatusCodes.Status200OK, ct);
    }

    private static string? CleanOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}

public sealed class UpdateAnimalRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid SpeciesId { get; set; }

    public Guid? BreedId { get; set; }

    // Null/omitted preserves the type; legacy records can be classified once.
    public AnimalTrackingType? TrackingType { get; set; }

    // Nullable distinguishes missing quantity from an explicitly empty group.
    public int? Quantity { get; set; }

    public string? ReferenceId { get; set; }

    // Missing Sex remains invalid instead of defaulting to Unknown.
    public AnimalSex Sex { get; set; } = (AnimalSex)(-1);

    public string? Color { get; set; }

    public DateOnly? Birthdate { get; set; }

    public Guid? AreaId { get; set; }
}

public sealed class UpdateAnimalResponse
{
    public Guid Revision { get; init; }

    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public Guid SpeciesId { get; init; }

    public Guid? BreedId { get; init; }

    public AnimalTrackingType TrackingType { get; init; }

    public int Quantity { get; init; }

    public string? ReferenceId { get; init; }

    public AnimalSex Sex { get; init; }

    public string? Color { get; init; }

    public DateOnly? Birthdate { get; init; }

    public Guid? AreaId { get; init; }

    public AnimalStatus Status { get; init; }

    public DateOnly? StatusEffectiveDate { get; init; }

    public string? StatusNotes { get; init; }
}

public sealed class Validator : Validator<UpdateAnimalRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision).NotEmpty().WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Animal ID must not be an empty GUID.");

        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Animal name is required.")
            .MaximumLength(100);

        RuleFor(req => req.SpeciesId)
            .NotEmpty()
            .WithMessage("A species must be selected.");

        RuleFor(req => req.BreedId)
            .Must(id => !id.HasValue || id.Value != Guid.Empty)
            .WithMessage(
                "BreedId must be a non-empty ID when provided, or null.");

        RuleFor(req => req.TrackingType)
            .Must(type => !type.HasValue
                || type.Value is AnimalTrackingType.Individual or AnimalTrackingType.Group)
            .WithMessage("TrackingType must be omitted, 1 (Individual), or 2 (Group).");

        RuleFor(req => req.Quantity)
            .NotNull()
            .WithMessage("Quantity must be supplied.")
            .GreaterThanOrEqualTo(0)
            .WithMessage("Quantity cannot be negative.");

        var allowedSexValues = string.Join(
            ", ",
            Enum.GetValues<AnimalSex>()
                .Select(value => $"{(int)value} ({value})"));

        RuleFor(req => req.Sex)
            .IsInEnum()
            .WithMessage(
                $"Sex must be supplied and must be one of: {allowedSexValues}.");

        RuleFor(req => req.AreaId)
            .Must(id => !id.HasValue || id.Value != Guid.Empty)
            .WithMessage(
                "AreaId must be a non-empty ID when provided, or null.");

        // Uses the UTC calendar date until farm-local time zones exist.
        RuleFor(req => req.Birthdate)
            .Must(date =>
                !date.HasValue
                || date.Value <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Birthdate must not be in the future.");
    }
}
