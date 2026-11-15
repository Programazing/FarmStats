namespace FarmStats.API.Features.Animal.AddAnimal;

public sealed class AddAnimal: Endpoint<AddAnimalRequest, AddAnimalResponse>
{
    public override void Configure()
    {
        Post("/animal");
        AllowAnonymous();
    }

    public override async Task HandleAsync(AddAnimalRequest r, CancellationToken c)
    {
        await Send.OkAsync(new AddAnimalResponse
        {
            Name = r.Name
        }, c);
    }
}

public sealed class AddAnimalResponse
{
    public required string Name { get; set; } 
}

public sealed class AddAnimalRequest
{
    public string Name { get; set; } = "";
}

public sealed class AddAnimalValidator : Validator<AddAnimalRequest>
{
    public AddAnimalValidator()
    {
        RuleFor(r => r.Name)
            .NotEmpty()
            .MaximumLength(100);
    }
}