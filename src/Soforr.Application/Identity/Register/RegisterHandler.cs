using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Persistence;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;

namespace Soforr.Application.Identity.Register;

/// <summary>Registers a user together with a new organization that they own.</summary>
public sealed record RegisterCommand(string? Email, string? Password, string? OrganizationName, string? Currency, string? TimeZoneId);

/// <summary>
/// Creates the user, the organization and the Owner membership in one transaction (ADR 0002, ADR 0003): either all
/// three exist afterwards or none does. The first user of an organization is its Owner.
/// </summary>
public sealed class RegisterHandler(IAppDbContext db, IUserAccounts accounts, IClock clock, IIdGenerator ids, IAuditLog audit)
    : ICommandHandler<RegisterCommand, AccountDto>
{
    public async Task<Result<AccountDto>> HandleAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.UtcNow;
        var organizationId = ids.NewId();
        var userId = ids.NewId();

        var organization = DomainErrors.Run(
            () => Organization.Create(organizationId, command.OrganizationName, Currency.Create(command.Currency), command.TimeZoneId, now),
            IdentityErrors.RegistrationFieldFor);
        if (organization.IsFailure)
        {
            return Result.Failure<AccountDto>(organization.Error!);
        }

        var staged = await accounts.StageNewUserAsync(userId, command.Email, command.Password, cancellationToken);
        if (staged.IsFailure)
        {
            return Result.Failure<AccountDto>(staged.Error!);
        }

        var membership = DomainErrors.Run(() => Membership.Create(ids.NewId(), userId, organizationId, Role.Owner, now));
        if (membership.IsFailure)
        {
            return Result.Failure<AccountDto>(membership.Error!);
        }

        db.Set<Organization>().Add(organization.Value);
        db.Set<Membership>().Add(membership.Value);

        // Audited in the same transaction: the registration and its record exist together or not at all.
        audit.Record(new AuditEntry(organizationId, AuditActions.UserRegistered, userId, "User", userId, "Account registered and organization created."));
        audit.Record(new AuditEntry(organizationId, AuditActions.RoleAssigned, userId, "Membership", membership.Value.Id, "Role Owner assigned on registration."));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two registrations with the same address can race past the validation; the database constraint then
            // decides. Anything else that failed to save is not ours to hide.
            if (!await accounts.EmailExistsAsync(command.Email!.Trim(), cancellationToken))
            {
                throw;
            }

            return Result.Failure<AccountDto>(IdentityErrors.EmailTaken);
        }

        return Result.Success(new AccountDto(userId, organizationId, Role.Owner));
    }
}
