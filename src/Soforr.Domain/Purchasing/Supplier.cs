using Soforr.Domain.Common;

namespace Soforr.Domain.Purchasing;

/// <summary>
/// Someone the business buys from. A supplier belongs to exactly one organization and is never deleted: once it has
/// purchase orders it is deactivated, so past orders keep a valid reference to it. Contact details are one optional
/// free-text field, kept minimal on purpose (personal data is minimized).
/// </summary>
public sealed class Supplier
{
    public const int MaxNameLength = 200;
    public const int MaxContactDetailsLength = 500;

    // Required by the persistence layer for materialization; not part of the domain API.
    private Supplier()
    {
        Name = string.Empty;
    }

    private Supplier(Guid id, Guid organizationId, string name, string? contactDetails, DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        ContactDetails = contactDetails;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public string? ContactDetails { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Supplier Create(Guid id, Guid organizationId, string? name, string? contactDetails, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("supplier.id_required", "A supplier must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("supplier.organization_required", "A supplier must belong to an organization.");
        }

        return new Supplier(id, organizationId, ValidName(name), ValidContactDetails(contactDetails), RequireUtc(createdAtUtc));
    }

    /// <summary>Replaces the name and contact details. Both are validated before anything changes.</summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public void Update(string? name, string? contactDetails, DateTimeOffset updatedAtUtc)
    {
        var validName = ValidName(name);
        var validContactDetails = ValidContactDetails(contactDetails);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        Name = validName;
        ContactDetails = validContactDetails;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>Takes the supplier out of use without deleting it. Deactivating an inactive supplier changes nothing.</summary>
    /// <exception cref="DomainException">The timestamp is not UTC.</exception>
    public void Deactivate(DateTimeOffset updatedAtUtc)
    {
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("supplier.name_required", "The supplier name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("supplier.name_too_long", $"The supplier name can have at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? ValidContactDetails(string? contactDetails)
    {
        var trimmed = string.IsNullOrWhiteSpace(contactDetails) ? null : contactDetails.Trim();

        if (trimmed is { Length: > MaxContactDetailsLength })
        {
            throw new DomainException("supplier.contact_details_too_long", $"The contact details can have at most {MaxContactDetailsLength} characters.");
        }

        return trimmed;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("supplier.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
