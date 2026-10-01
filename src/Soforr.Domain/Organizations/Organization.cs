using Soforr.Domain.Common;

namespace Soforr.Domain.Organizations;

/// <summary>
/// A tenant. Every tenant-owned record in the system belongs to exactly one organization.
/// The currency is fixed at creation: changing it once transactions exist would silently reinterpret money,
/// so it is not modifiable until a rule that accounts for existing transactions is designed.
/// </summary>
public sealed class Organization
{
    public const int MaxNameLength = 200;

    // Required by the persistence layer for materialization; not part of the domain API.
    private Organization()
    {
        Name = string.Empty;
        Currency = null!;
        TimeZoneId = string.Empty;
    }

    private Organization(Guid id, string name, Currency currency, string timeZoneId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        Currency = currency;
        TimeZoneId = timeZoneId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>An IANA time zone identifier supported by the runtime's time zone database, for example <c>Europe/London</c>.</summary>
    public string TimeZoneId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Organization Create(Guid id, string? name, Currency currency, string? timeZoneId, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (id == Guid.Empty)
        {
            throw new DomainException("organization.id_required", "An organization must have an identifier.");
        }

        return new Organization(id, ValidName(name), currency, ValidTimeZoneId(timeZoneId), RequireUtc(createdAtUtc));
    }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public void UpdateSettings(string? name, string? timeZoneId, DateTimeOffset updatedAtUtc)
    {
        var validName = ValidName(name);
        var validTimeZoneId = ValidTimeZoneId(timeZoneId);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        Name = validName;
        TimeZoneId = validTimeZoneId;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("organization.name_required", "The organization name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("organization.name_too_long", $"The organization name can have at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string ValidTimeZoneId(string? timeZoneId)
    {
        var trimmed = timeZoneId?.Trim();

        if (string.IsNullOrEmpty(trimmed) || !IsSupportedIanaId(trimmed))
        {
            throw new DomainException("organization.timezone_invalid", "The time zone must be a supported IANA time zone identifier.");
        }

        return trimmed;
    }

    /// <summary>
    /// Soforr stores IANA time zone identifiers and validates that the identifier is supported by the application's
    /// runtime time zone database. The runtime lookup alone is not enough: it also accepts Windows identifiers, and
    /// it matches case-insensitively once a zone has been cached, so the same input could pass or fail depending on
    /// earlier lookups. Requiring an IANA-shaped identifier that equals the identifier the runtime reports keeps the
    /// result deterministic and the stored value canonical.
    /// </summary>
    private static bool IsSupportedIanaId(string timeZoneId)
    {
        if (!HasIanaShape(timeZoneId))
        {
            return false;
        }

        try
        {
            return string.Equals(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).Id, timeZoneId, StringComparison.Ordinal);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    // Letters, digits, '/', '_', '-' and '+' only: this excludes spaces (Windows identifiers), dots, backslashes and
    // control characters, so no path-like value can reach the runtime lookup.
    private static bool HasIanaShape(string timeZoneId) =>
        timeZoneId.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+')
        && !timeZoneId.StartsWith('/')
        && !timeZoneId.EndsWith('/')
        && !timeZoneId.Contains("//", StringComparison.Ordinal);

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("organization.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
