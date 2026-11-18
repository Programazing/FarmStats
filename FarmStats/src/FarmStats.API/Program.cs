global using FastEndpoints;
global using FluentValidation;

using FarmStats.API.Data;
using FarmStats.API.Data.Seeding;
using FastEndpoints.Swagger;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddFastEndpoints()
    .SwaggerDocument();

var connectionString =
    builder.Configuration.GetConnectionString("FarmStats")
    ?? throw new InvalidOperationException(
        "Connection string 'FarmStats' is not configured.");

var includeDevelopmentAreas =
    builder.Environment.IsDevelopment();

builder.Services.AddDbContext<FarmStatsDbContext>(options =>
{
    options.UseNpgsql(connectionString);

    options.UseSeeding((context, _) =>
    {
        ReferenceDataSeeder.Seed(
            (FarmStatsDbContext)context,
            includeDevelopmentAreas);
    });

    options.UseAsyncSeeding(async (context, _, ct) =>
    {
        await ReferenceDataSeeder.SeedAsync(
            (FarmStatsDbContext)context,
            includeDevelopmentAreas,
            ct);
    });
});

var app = builder.Build();

app.UseHttpsRedirection();

app.UseFastEndpoints(config =>
{
    config.Errors.UseProblemDetails();
});

if (app.Environment.IsDevelopment())
{
    app.UseOpenApi(options =>
    {
        options.Path = "/openapi/{documentName}.json";
    });

    app.MapScalarApiReference();
}

app.Run();