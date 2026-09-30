using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Soforr.Api.Hosting;
using Soforr.Application;
using Soforr.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseApiLogging();

builder.Services.AddOpenApi();
builder.Services.AddApiErrorHandling();
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
});

app.Run();

public partial class Program;
