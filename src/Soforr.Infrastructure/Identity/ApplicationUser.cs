using Microsoft.AspNetCore.Identity;

namespace Soforr.Infrastructure.Identity;

/// <summary>
/// The credential record of a person who can sign in (ASP.NET Core Identity). It holds the password hash, lockout
/// state and security stamp and nothing else: the Domain and the Application never see it. Which organization the
/// user belongs to and with which role is the Domain's <see cref="Soforr.Domain.Identity.Membership"/>.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>;
