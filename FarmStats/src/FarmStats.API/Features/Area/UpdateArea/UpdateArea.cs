using FarmStats.API.Data;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.UpdateArea;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<UpdateAreaRequest, UpdateAreaResponse>
{
    public override void Configure()
    {
        Put("/areas/{Id}");
        Description(builder => builder.WithTags("Area"));
        
        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Update a area";
            summary.Response<UpdateAreaResponse>(200, "The saved area.");
            summary.Response(400, "Invalid input or lookup relationships.");
            summary.Response(404, "The record does not exist.");
        });
    }

    public override async Task HandleAsync(UpdateAreaRequest req, CancellationToken ct)
    {
        // For real farm data, scope the lookup, duplicate check, and write
        // to the authenticated farm. Do not accept farm ownership from the client.

        var entity = await db.Areas
            .SingleOrDefaultAsync(item => item.Id == req.Id, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync();
            return;
        }

        var name = req.Name.Trim();
        var normalizedName = name.ToUpperInvariant();

        var duplicateExists = await db.Areas
            .AnyAsync(item => item.Id != req.Id
                    && item.Name.Trim().ToUpper() == normalizedName, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A area with this name already exists.");
        }

        ThrowIfAnyErrors();

        entity.Name = name;

        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new UpdateAreaResponse
        {
            Id = entity.Id,
            Name = entity.Name
        }, 200, ct);
    }
}

public sealed class UpdateAreaRequest
{
    [RouteParam]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class UpdateAreaResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class UpdateAreaValidator : Validator<UpdateAreaRequest>
{
    public UpdateAreaValidator()
    {
        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Area ID must not be an empty GUID.");

        // Application-level limit. Keep aligned with the entity/database schema.
        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Area name is required.")
            .MaximumLength(100);
    }
}
