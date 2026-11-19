using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Area.AddArea;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<AddAreaRequest, AddAreaResponse>
{
    public override void Configure()
    {
        Post("/area");
        Description(builder => builder.WithTags("Area"));

        // Local development only. Add authorization before deployment.
        AllowAnonymous();

        Summary(summary =>
        {
            summary.Summary = "Add an area";
            summary.Response<AddAreaResponse>(201, "The saved area.");
            summary.Response(400, "Invalid input or lookup relationships.");
        });
    }

    public override async Task HandleAsync(AddAreaRequest req, CancellationToken ct)
    {
        // For real farm data, scope the lookup, duplicate check, and write
        // to the authenticated farm. Do not accept farm ownership from the client.

        var name = req.Name.Trim();
        var normalizedName = name.ToUpperInvariant();

        var duplicateExists = await db.Areas
            .AnyAsync(item => item.Name.Trim().ToUpper() == normalizedName, ct);

        if (duplicateExists)
        {
            AddError(request => request.Name,
                "A area with this name already exists.");
        }

        ThrowIfAnyErrors();

        // Target-typed construction uses the entity type from the DbSet.
        var entity = db.Areas.Add(new()
        {
            Id = Guid.NewGuid(),
            Name = name
        }).Entity;

        await db.SaveChangesAsync(ct);

        await Send.ResponseAsync(new AddAreaResponse
        {
            Id = entity.Id,
            Name = entity.Name
        }, 201, ct);
    }
}

public sealed class AddAreaRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class AddAreaResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class AddAreaValidator : Validator<AddAreaRequest>
{
    public AddAreaValidator()
    {
        // Application-level limit. Keep aligned with the entity/database schema.
        RuleFor(req => req.Name)
            .NotEmpty()
            .WithMessage("Area name is required.")
            .MaximumLength(100);
    }
}