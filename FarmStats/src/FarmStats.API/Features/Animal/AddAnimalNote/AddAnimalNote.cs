using FarmStats.API.Data;
using FarmStats.API.Data.Entities;
using FarmStats.API.Features.Animal.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Features.Animal.AddAnimalNote;

public sealed class Endpoint(FarmStatsDbContext db)
    : Endpoint<CreateAnimalNoteRequest, CreateAnimalNoteResponse>
{
    public override void Configure()
    {
        Post("/animal/{Id}/notes");
        Description(builder => builder.WithTags("Animal"));
        AllowAnonymous(); // Development only.

        Summary(summary =>
        {
            summary.Summary = "Add a dated note to an animal";
            summary.Description =
                "Supply the animal's current revision; stale revisions return 409. " +
                "Notes attach to active or inactive animals. Deleted animals return 404. " +
                "Text is trimmed and limited to 2000 characters. " +
                "EffectiveDate cannot be in the future (UTC). " +
                "Returns the created note and the animal's new revision.";
            summary.Response<CreateAnimalNoteResponse>(
                201, "The created note and updated animal revision.");
            summary.Response(400, "Invalid text, effective date, or revision.");
            summary.Response(404, "Animal not found or deleted.");
            summary.Response(409, "Concurrent modification; reload and retry.");
        });
    }

    public override async Task HandleAsync(
        CreateAnimalNoteRequest req,
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

        var cleanedText = CleanText(req.Text);
        if (string.IsNullOrEmpty(cleanedText))
        {
            AddError(r => r.Text, "Note text is required.");
        }
        else if (cleanedText.Length > 2000)
        {
            AddError(r => r.Text, "Note text cannot exceed 2000 characters after trimming.");
        }

        var maxEffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow);
        if (req.EffectiveDate > maxEffectiveDate)
        {
            AddError(r => r.EffectiveDate,
                "Effective date cannot be in the future (UTC).");
        }

        ThrowIfAnyErrors();

        var note = new AnimalNote
        {
            AnimalId = animal.Id,
            Text = cleanedText!,
            EffectiveDate = req.EffectiveDate,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        db.Entry(animal).Property(a => a.Revision).OriginalValue = req.Revision;
        db.Entry(animal).Property(a => a.Revision).IsModified = true;

        db.AnimalNotes.Add(note);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await AnimalLifecycleSupport.WriteConcurrencyProblem(HttpContext);
            return;
        }

        await Send.ResponseAsync(new CreateAnimalNoteResponse
        {
            Id = animal.Id,
            Revision = animal.Revision,
            NoteId = note.Id,
            Text = note.Text,
            EffectiveDate = note.EffectiveDate,
            RecordedAtUtc = note.RecordedAtUtc
        }, 201, ct);
    }

    private static string? CleanText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CreateAnimalNoteRequest
{
    public Guid Revision { get; set; }

    [RouteParam]
    public Guid Id { get; set; }

    public string? Text { get; set; }

    public DateOnly EffectiveDate { get; set; }
}

public sealed class Validator : Validator<CreateAnimalNoteRequest>
{
    public Validator()
    {
        RuleFor(req => req.Revision)
            .NotEmpty()
            .WithMessage("A non-empty revision is required.");

        RuleFor(req => req.Id)
            .NotEmpty()
            .WithMessage("Animal ID must not be an empty GUID.");

        RuleFor(req => req.EffectiveDate)
            .NotEmpty()
            .WithMessage("Effective date is required.");
    }
}