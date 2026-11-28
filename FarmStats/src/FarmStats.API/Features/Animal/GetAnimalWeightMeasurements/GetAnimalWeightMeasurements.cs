using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetAnimalWeightMeasurements;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<GetAnimalWeightMeasurementsRequest, GetAnimalWeightMeasurementsResponse>
{
    public override void Configure()
    {
        Get("/animal/{Id}/weight-measurements");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Get paginated weight measurements for an animal";
            summary.Description =
                "Returns measurements for the specified animal, including for soft-deleted records. " +
                "Ordered by MeasurementDate DESC, RecordedAtUtc DESC, Id ASC. " +
                "Includes a LatestWeight summary computed over all measurements. " +
                "Pagination uses the standard conventions with overflow protection.";
            summary.Response<GetAnimalWeightMeasurementsResponse>(
                200, "A page of measurements, latest weight summary, and the current entity revision.");
            summary.Response(400, "Invalid ID or pagination parameters.");
            summary.Response(404, "Animal not found.");
        });
    }

    public override async Task HandleAsync(
        GetAnimalWeightMeasurementsRequest req,
        CancellationToken ct)
    {
        var revision = await db.Animals.IgnoreQueryFilters()
            .Where(a => a.Id == req.Id)
            .Select(a => (Guid?)a.Revision)
            .FirstOrDefaultAsync(ct);

        if (revision is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        var query = db.AnimalWeightMeasurements
            .Where(m => m.AnimalId == req.Id);

        var count = await query.CountAsync(ct);
        var skip = (int)(((long)req.Page - 1) * req.PageSize);

        var items = await query
            .OrderByDescending(m => m.MeasurementDate)
            .ThenByDescending(m => m.RecordedAtUtc)
            .ThenBy(m => m.Id)
            .Skip(skip)
            .Take(req.PageSize)
            .Select(m => new AnimalWeightMeasurementItem
            {
                Id = m.Id,
                Value = m.Value,
                Unit = m.Unit,
                MeasurementDate = m.MeasurementDate,
                RecordedAtUtc = m.RecordedAtUtc,
                ActorUserId = m.ActorUserId
            })
            .ToListAsync(ct);

        var latestMeasurement = await db.AnimalWeightMeasurements
            .Where(m => m.AnimalId == req.Id)
            .OrderByDescending(m => m.MeasurementDate)
            .ThenByDescending(m => m.RecordedAtUtc)
            .ThenBy(m => m.Id)
            .Select(m => new LatestWeightSummary
            {
                MeasurementId = m.Id,
                Value = m.Value,
                Unit = m.Unit,
                MeasurementDate = m.MeasurementDate,
                RecordedAtUtc = m.RecordedAtUtc
            })
            .FirstOrDefaultAsync(ct);

        var totalPages = (int)Math.Ceiling((double)count / req.PageSize);

        await Send.ResponseAsync(new GetAnimalWeightMeasurementsResponse
        {
            Revision = revision.Value,
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalCount = count,
            TotalPages = totalPages,
            HasPreviousPage = req.Page > 1,
            HasNextPage = req.Page < totalPages,
            LatestWeight = latestMeasurement
        }, 200, ct);
    }
}

public sealed class GetAnimalWeightMeasurementsRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 25;
}

public sealed class Validator : Validator<GetAnimalWeightMeasurementsRequest>
{
    public Validator()
    {
        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Animal ID must not be an empty GUID.");

        RuleFor(req => req.Page)
            .Cascade(CascadeMode.Stop)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Page must be at least 1.")
            .Must((req, page) =>
                ((long)page - 1) * req.PageSize <= int.MaxValue)
            .WithMessage("The requested page is too large.");

        RuleFor(req => req.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Page size must be between 1 and 100.");
    }
}