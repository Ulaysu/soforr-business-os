using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Identity.Register;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Identity;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Identity;

/// <summary>The registration transaction on a real database.</summary>
[Trait("Category", "Integration")]
public sealed class RegisterHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class SequentialIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    /// <summary>
    /// Stages a user without any validation, like two registrations that both passed the duplicate check before
    /// either was saved: only the database constraint is left to stop the second one.
    /// </summary>
    private sealed class UncheckedAccounts(AppDbContext db, string existingEmail) : IUserAccounts
    {
        public Task<Result<Guid>> StageNewUserAsync(Guid userId, string? email, string? password, CancellationToken cancellationToken)
        {
            db.Set<ApplicationUser>().Add(new ApplicationUser
            {
                Id = userId,
                UserName = existingEmail,
                Email = existingEmail,
                NormalizedUserName = existingEmail.ToUpperInvariant(),
                NormalizedEmail = existingEmail.ToUpperInvariant(),
                SecurityStamp = "stamp",
            });
            return Task.FromResult(Result.Success(userId));
        }

        public async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
        {
            var normalized = email.ToUpperInvariant();
            return await db.Set<ApplicationUser>().AsNoTracking().AnyAsync(user => user.NormalizedEmail == normalized, cancellationToken);
        }
    }

    [Fact]
    public async Task WhenTheDatabaseRefusesTheUser_NothingOfTheRegistrationIsStored_AndTheResultIsAConflict()
    {
        var email = $"raced-{Guid.NewGuid():N}@example.com";
        await using (var seed = database.CreateContext())
        {
            seed.Set<ApplicationUser>().Add(new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                NormalizedUserName = email.ToUpperInvariant(),
                NormalizedEmail = email.ToUpperInvariant(),
                SecurityStamp = "stamp",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        await using var verify = database.CreateContext();
        var organizationsBefore = await verify.Set<Organization>().IgnoreQueryFilters().CountAsync();
        var handler = new RegisterHandler(db, new UncheckedAccounts(db, email), new FixedClock(), new SequentialIds());

        var result = await handler.HandleAsync(new RegisterCommand(email, "correct horse battery staple", "Soforr", "GMD", "UTC"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("identity.email_taken", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        await using var after = database.CreateContext();
        Assert.Equal(organizationsBefore, await after.Set<Organization>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await after.Set<ApplicationUser>().CountAsync(user => user.Email == email));
    }
}
