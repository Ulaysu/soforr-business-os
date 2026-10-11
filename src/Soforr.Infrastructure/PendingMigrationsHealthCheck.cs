using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Infrastructure;

/// <summary>
/// The application is ready only when the database has every migration the code was built for. The connection check alone says
/// "healthy" for an empty database, and the first real request then fails with a missing table. Migrations are applied explicitly
/// (the application never migrates itself), so this is what tells an operator, a container platform or a developer that the step was
/// forgotten. The response carries no detail.
/// </summary>
internal sealed class PendingMigrationsHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return pending.Any()
                ? HealthCheckResult.Unhealthy("The database has migrations that have not been applied.")
                : HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database migrations could not be checked.", exception);
        }
    }
}
