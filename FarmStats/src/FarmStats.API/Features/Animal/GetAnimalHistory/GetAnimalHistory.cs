using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetAnimalHistory;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<HistoryRequest, AnimalHistoryResponse>
{
    public override void Configure()
    {
        Get("/animal/{Id}/history");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only; history can include deleted animals.

        Summary(summary =>
        {
            summary.Summary = "Get animal lifecycle history";
            summary.Response<AnimalHistoryResponse>(200, "A page of history.");
            summary.Response(400, "Invalid ID or pagination.");
            summary.Response(404, "Animal not found.");
        });
    }

    public override async Task HandleAsync(
        HistoryRequest req,
        CancellationToken ct)
    {
        // Include deleted animals so their audit trail remains accessible.
        // This is NOT a tenant-safe query once farm filters are introduced.
        var exists = await db.Animals.IgnoreQueryFilters()
            .AnyAsync(animal => animal.Id == req.Id, ct);

        if (!exists)
        {
            await Send.NotFoundAsync();
            return;
        }

        var query = db.AnimalHistory
            .Where(entry => entry.AnimalId == req.Id);

        

        var count = await query.CountAsync(ct);
        var skip = (int)(((long)req.Page - 1) * req.PageSize);

        var items = await query
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .ThenBy(entry => entry.Id)
            .Skip(skip)
            .Take(req.PageSize)
            .Select(entry => new AnimalHistoryItem
            {
                Id = entry.Id,
                EventType = entry.EventType,
                PreviousStatus = entry.PreviousStatus,
                NewStatus = entry.NewStatus,
                EffectiveDate = entry.EffectiveDate,
                RecordedAtUtc = entry.RecordedAtUtc,
                DeletionReason = entry.DeletionReason,
                Notes = entry.Notes,
                ActorUserId = entry.ActorUserId
            })
            .ToListAsync(ct);

        await Send.ResponseAsync(new AnimalHistoryResponse
        {
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalCount = count
        }, 200, ct);
    }
}

public sealed class HistoryRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 25;
}

public sealed class Validator : Validator<HistoryRequest>
{
    public Validator()
    {
        RuleFor(req => req.Id).NotEmpty();

        RuleFor(req => req.Page)
            .Cascade(CascadeMode.Stop)
            .GreaterThanOrEqualTo(1)
            .Must((req, page) =>
                ((long)page - 1) * req.PageSize <= int.MaxValue);

        RuleFor(req => req.PageSize).InclusiveBetween(1, 100);
    }
}
