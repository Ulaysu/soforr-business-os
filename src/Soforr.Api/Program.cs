using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Soforr.Api.Hosting;
using Soforr.Api.Organizations;
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

// Development only until authentication and authorization exist (IDENTITY-002): these endpoints have no access
// control yet, so they must not be reachable in any other environment.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapOrganizationEndpoints();
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
