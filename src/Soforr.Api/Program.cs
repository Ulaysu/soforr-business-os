using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Soforr.Api.Hosting;
using Soforr.Api.Identity;
using Soforr.Api.Organizations;
using Soforr.Application;
using Soforr.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseApiLogging();

builder.Services.AddOpenApi();
builder.Services.AddApiJson();
builder.Services.AddApiErrorHandling();
builder.Services.AddApiSecurity();
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline();
app.UseAuthentication();
app.UseAuthorization();

// Development only for now. The organization endpoints have no access control until IDENTITY-002. The
// authentication endpoints allow anyone to register, which ADR 0002 permits only after e-mail verification
// (IDENTITY-004) and rate limiting (SECURITY-002) exist, so neither group is reachable in any other environment.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapAuthEndpoints();
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
