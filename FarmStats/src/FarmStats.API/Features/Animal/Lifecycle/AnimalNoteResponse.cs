namespace FarmStats.API.Features.Animal.Lifecycle;

public sealed class AnimalNoteItem
{
    public Guid Id { get; init; }

    public string Text { get; init; } = string.Empty;

    public DateOnly EffectiveDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }

    public string? ActorUserId { get; init; }
}

public sealed class GetAnimalNotesResponse
{
    public Guid Revision { get; init; }

    public List<AnimalNoteItem> Items { get; init; } = [];

    public int Page { get; init; }

    public int PageSize { get; init; }

    public int TotalCount { get; init; }

    public int TotalPages =>
        (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

public sealed class CreateAnimalNoteResponse
{
    public Guid Id { get; init; }

    public Guid Revision { get; init; }

    public Guid NoteId { get; init; }

    public string Text { get; init; } = string.Empty;

    public DateOnly EffectiveDate { get; init; }

    public DateTimeOffset RecordedAtUtc { get; init; }
}