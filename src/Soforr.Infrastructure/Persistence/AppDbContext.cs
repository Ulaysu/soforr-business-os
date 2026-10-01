using Microsoft.EntityFrameworkCore;
using Soforr.Application.Persistence;

namespace Soforr.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money is numeric(19,4); quantities (numeric(18,4)) are configured explicitly when they are introduced.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
    }
}
