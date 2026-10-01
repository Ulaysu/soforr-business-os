using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Persistence;
using Soforr.Infrastructure.Identity;

namespace Soforr.Infrastructure.Persistence;

/// <summary>
/// The application's database. It is also the Identity store (users, claims, logins, tokens; no Identity roles,
/// because a user's role is the Domain's membership, ADR 0002), so the credential record, the organization and the
/// membership can be saved in one transaction.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options), IAppDbContext
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);

        // Table names match the rest of the schema (no "AspNet" prefix).
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens");

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // Money is numeric(19,4); quantities (numeric(18,4)) are configured explicitly when they are introduced.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
    }
}
