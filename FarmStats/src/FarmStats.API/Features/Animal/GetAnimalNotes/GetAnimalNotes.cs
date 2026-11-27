using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.GetAnimalNotes;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<GetAnimalNotesRequest, GetAnimalNotesResponse>
{
    public override void Configure()
    {
        Get("/animal/{Id}/notes");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Get paginated notes for an animal";
            summary.Description =
                "Returns notes for the specified animal, including for soft-deleted records. " +
                "Ordered by RecordedAtUtc descending, then Id ascending. " +
                "Pagination uses the standard conventions with overflow protection.";
            summary.Response<GetAnimalNotesResponse>(
                200, "A page of notes and the current entity revision.");
            summary.Response(400, "Invalid ID or pagination parameters.");
            summary.Response(404, "Animal not found.");
        });
    }

    public override async Task HandleAsync(
        GetAnimalNotesRequest req,
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

        var query = db.AnimalNotes
            .Where(n => n.AnimalId == req.Id);

        var count = await query.CountAsync(ct);
        var skip = (int)(((long)req.Page - 1) * req.PageSize);

        var items = await query
            .OrderByDescending(n => n.RecordedAtUtc)
            .ThenBy(n => n.Id)
            .Skip(skip)
            .Take(req.PageSize)
            .Select(n => new AnimalNoteItem
            {
                Id = n.Id,
                Text = n.Text,
                EffectiveDate = n.EffectiveDate,
                RecordedAtUtc = n.RecordedAtUtc,
                ActorUserId = n.ActorUserId
            })
            .ToListAsync(ct);

        await Send.ResponseAsync(new GetAnimalNotesResponse
        {
            Revision = revision.Value,
            Items = items,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalCount = count
        }, 200, ct);
    }
}

public sealed class GetAnimalNotesRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 25;
}

public sealed class Validator : Validator<GetAnimalNotesRequest>
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