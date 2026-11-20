using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Area.GetAreaHistory;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<HistoryRequest, HistoryResponse>
{
    public override void Configure()
    {
        Get("/area/{Id}/history");
        Description(builder => builder.WithTags("Area"));
        AllowAnonymous(); // Development only; includes deleted lookups.
        Summary(summary =>
        {
            summary.Summary = "Get area deletion and restoration history";
            summary.Response<HistoryResponse>(200, "A page of audit entries and the current entity revision, including for deleted lookups. The revision can become stale after this read.");
            summary.Response(400, "Invalid ID or pagination.");
            summary.Response(404, "Lookup not found.");
        });
    }

    public override async Task HandleAsync(HistoryRequest req, CancellationToken ct)
    {
        // Retain farm scope here when tenant filters are introduced.
        var revision = await db.Areas.IgnoreQueryFilters().Where(e => e.Id == req.Id)
            .Select(e => (Guid?)e.Revision).FirstOrDefaultAsync(ct);
        if (revision is null)
        {
            await Send.NotFoundAsync();
            return;
        }
        var query = db.LookupHistory.Where(h =>
            h.LookupKind == LookupKind.Area && h.LookupId == req.Id);
        var count = await query.CountAsync(ct);
        var skip = (int)(((long)req.Page - 1) * req.PageSize);
        var items = await query.AsNoTracking()
            .OrderByDescending(h => h.RecordedAtUtc).ThenBy(h => h.Id)
            .Skip(skip).Take(req.PageSize).ToListAsync(ct);
        await Send.ResponseAsync(new HistoryResponse
        {
            Revision = revision.Value, Items = items, Page = req.Page, PageSize = req.PageSize, TotalCount = count
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

public sealed class HistoryResponse
{
    public Guid Revision { get; init; }
    public List<LookupHistoryEntry> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class Validator : Validator<HistoryRequest>
{
    public Validator()
    {
        RuleFor(r => r.Id).NotEmpty();
        RuleFor(r => r.Page).Cascade(CascadeMode.Stop).GreaterThanOrEqualTo(1)
            .Must((r, page) => ((long)page - 1) * r.PageSize <= int.MaxValue);
        RuleFor(r => r.PageSize).InclusiveBetween(1, 100);
    }
}
