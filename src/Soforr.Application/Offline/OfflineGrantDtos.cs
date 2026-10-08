using Soforr.Domain.Offline;

namespace Soforr.Application.Offline;

/// <summary>
/// A grant as the client sees it. <see cref="GrantId"/> is an identifier, not a secret. <see cref="ExpiresAtUtc"/> is when the grant stops
/// accepting contact if none arrives (the last contact plus the offline window, never past <see cref="AbsoluteExpiresAtUtc"/>);
/// <see cref="ServerTimeUtc"/> is the server clock at the response. The device clock is never used for any of these.
/// </summary>
public sealed record OfflineGrantDto(
    Guid GrantId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset LastContactAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    int WindowSeconds,
    int ContactIntervalSeconds,
    DateTimeOffset ServerTimeUtc)
{
    internal static OfflineGrantDto From(OfflineGrant grant, DateTimeOffset lastContactUtc, DateTimeOffset serverTimeUtc, IOfflineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(settings);

        var expires = lastContactUtc + settings.Policy.Window;

        return new OfflineGrantDto(
            grant.Id,
            grant.IssuedAtUtc,
            lastContactUtc,
            expires < grant.AbsoluteExpiresAtUtc ? expires : grant.AbsoluteExpiresAtUtc,
            grant.AbsoluteExpiresAtUtc,
            (int)settings.Policy.Window.TotalSeconds,
            (int)settings.ContactInterval.TotalSeconds,
            serverTimeUtc);
    }
}
