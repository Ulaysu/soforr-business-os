using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Soforr.Api.Audit;
using Soforr.Api.Catalog;
using Soforr.Api.Hosting;
using Soforr.Api.Identity;
using Soforr.Api.Inventory;
using Soforr.Api.Organizations;
using Soforr.Api.Purchasing;
using Soforr.Api.Sales;
using Soforr.Application;
using Soforr.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseApiLogging();

builder.Services.AddOpenApi();
builder.Services.AddApiJson();
builder.Services.AddApiErrorHandling();
builder.Services.AddApiSecurity();
builder.Services.AddScoped<Soforr.Application.Audit.ICorrelationContext, Soforr.Api.Observability.HttpCorrelationContext>();
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline();
app.UseAuthentication();
app.UseAuthorization();

// The authentication endpoints let anyone register, which ADR 0002 permits only after e-mail verification
// (IDENTITY-004) and rate limiting (SECURITY-002) exist, so they (and the OpenAPI document) are mapped only in
// Development. The organization endpoints require a signed-in user with the right permission, so they are mapped
// everywhere; where no one can sign in they answer 401.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapAuthEndpoints();
}

app.MapOrganizationEndpoints();
app.MapProductEndpoints();
app.MapAuditEndpoints();
app.MapInventoryEndpoints();
app.MapPurchasingEndpoints();
app.MapSalesEndpoints();

// The default-deny policy also applies to requests that match no endpoint, which would answer 401 for every
// unknown path. A route that does not exist must stay a 404 (it is not a secret and it is not a sign-in problem),
// so a catch-all endpoint answers those requests.
app.MapFallback("/{**path}", () => Results.NotFound()).AllowAnonymous();

app.UseHttpsRedirection();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
}).AllowAnonymous();

app.Run();

public partial class Program;
