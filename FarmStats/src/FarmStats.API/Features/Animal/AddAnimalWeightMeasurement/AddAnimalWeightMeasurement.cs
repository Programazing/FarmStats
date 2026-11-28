using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.AddAnimalWeightMeasurement;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<CreateAnimalWeightMeasurementRequest, CreateAnimalWeightMeasurementResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/weight-measurements");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Add a dated weight measurement to an animal";
            summary.Description =
                "Supply the animal's current revision; stale revisions return 409. " +
                "Measurements attach to active or inactive animals. Deleted animals return 404. " +
                "Value must be positive with at most three decimal places. " +
                "Unit must be explicitly supplied (Kilogram or Pound). " +
                "MeasurementDate cannot be in the future (UTC). " +
                "Returns the created measurement and the animal's new revision.";
            summary.Response<CreateAnimalWeightMeasurementResponse>(
                201, "The created measurement and updated animal revision.");
            summary.Response(400, "Invalid value, unit, measurement date, or revision.");
            summary.Response(404, "Animal not found or deleted.");
            summary.Response(409, "Concurrent modification; reload and retry.");
        });
    }

    public override async Task HandleAsync(
        CreateAnimalWeightMeasurementRequest req,
        CancellationToken ct)
    {
        var animal = await db.Animals
            .SingleOrDefaultAsync(a => a.Id == req.Id, ct);

        if (animal is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (animal.Revision != req.Revision)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        if (animal.DeletedAtUtc is not null)
        {
            await Send.NotFoundAsync();
            return;
        }

        if (req.Value <= 0)
        {
            AddError(r => r.Value, "Value must be positive.");
        }
        else if (req.Value > 9999999.999m)
        {
            AddError(r => r.Value, "Value exceeds the maximum supported value.");
        }
        else if (decimal.Round(req.Value, 3) != req.Value)
        {
            AddError(r => r.Value, "Value cannot have more than three decimal places.");
        }

        if (req.Unit is null)
        {
            AddError(r => r.Unit, "Unit is required.");
        }
        else if (!Enum.IsDefined(typeof(WeightUnit), req.Unit.Value))
        {
            AddError(r => r.Unit, "Unit must be a valid WeightUnit value (0 = Kilogram, 1 = Pound).");
        }

        var maxMeasurementDate = DateOnly.FromDateTime(DateTime.UtcNow);
        if (req.MeasurementDate > maxMeasurementDate)
        {
            AddError(r => r.MeasurementDate,
                "Measurement date cannot be in the future (UTC).");
        }

        ThrowIfAnyErrors();

        var measurement = new AnimalWeightMeasurement
        {
            AnimalId = animal.Id,
            Value = req.Value,
            Unit = req.Unit!.Value,
            MeasurementDate = req.MeasurementDate,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        db.Entry(animal).Property(a => a.Revision).OriginalValue = req.Revision;
        db.Entry(animal).Property(a => a.Revision).IsModified = true;

        db.AnimalWeightMeasurements.Add(measurement);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        await Send.ResponseAsync(new CreateAnimalWeightMeasurementResponse
        {
            Id = animal.Id,
            Revision = animal.Revision,
            MeasurementId = measurement.Id,
            Value = measurement.Value,
            Unit = measurement.Unit,
            MeasurementDate = measurement.MeasurementDate,
            RecordedAtUtc = measurement.RecordedAtUtc
        }, 201, ct);
    }
}

public sealed class CreateAnimalWeightMeasurementRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public decimal Value { get; set; }

    public WeightUnit? Unit { get; set; }

    public DateOnly MeasurementDate { get; set; }
}

public sealed class Validator : Validator<CreateAnimalWeightMeasurementRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision)
            .NotEmpty()
            .WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Animal ID must not be an empty GUID.");

        RuleFor(req => req.Value)
            .GreaterThan(0)
            .WithMessage("Value must be positive.")
            .LessThanOrEqualTo(9999999.999m)
            .WithMessage("Value exceeds the maximum supported value.");

        RuleFor(req => req.Unit)
            .NotNull()
            .WithMessage("Unit is required.")
            .IsInEnum()
            .WithMessage("Unit must be a valid WeightUnit value (0 = Kilogram, 1 = Pound).");

        RuleFor(req => req.MeasurementDate)
            .NotEmpty()
            .WithMessage("Measurement date is required.")
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Measurement date cannot be in the future (UTC).");
    }
}