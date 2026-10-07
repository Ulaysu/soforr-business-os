using Soforr.Domain.Common;

namespace Soforr.Domain.Customers;

/// <summary>
/// Someone the business sells to. A customer belongs to exactly one organization and is never deleted: once it has sales it
/// is deactivated, so past sales keep a valid reference to it. Contact details are one optional free-text field, kept minimal
/// on purpose (personal data is minimized). <see cref="CreditTermsDays"/> is the number of days after a sale until what the
/// customer owes for it is overdue; it is optional, and without it the organization's default applies (that default belongs to
/// the balance and aging rules of CUSTOMER-002 and INTEL-005, not to this entity).
/// </summary>
public sealed class Customer
{
    public const int MaxNameLength = 200;
    public const int MaxContactDetailsLength = 500;

    /// <summary>The longest credit term: a year. Zero is allowed and means due immediately.</summary>
    public const int MaxCreditTermsDays = 365;

    // Required by the persistence layer for materialization; not part of the domain API.
    private Customer()
    {
        Name = string.Empty;
    }

    private Customer(Guid id, Guid organizationId, string name, string? contactDetails, int? creditTermsDays, DateTimeOffset createdAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        ContactDetails = contactDetails;
        CreditTermsDays = creditTermsDays;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public string? ContactDetails { get; private set; }

    /// <summary>Days after a sale until the customer's balance for it is overdue; null when the customer has no terms of their own.</summary>
    public int? CreditTermsDays { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <exception cref="DomainException">An invariant is violated.</exception>
    public static Customer Create(Guid id, Guid organizationId, string? name, string? contactDetails, int? creditTermsDays, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("customer.id_required", "A customer must have an identifier.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new DomainException("customer.organization_required", "A customer must belong to an organization.");
        }

        return new Customer(id, organizationId, ValidName(name), ValidContactDetails(contactDetails), ValidCreditTerms(creditTermsDays), RequireUtc(createdAtUtc));
    }

    /// <summary>Replaces the name, contact details and credit terms. Everything is validated before anything changes.</summary>
    /// <exception cref="DomainException">An invariant is violated.</exception>
    public void Update(string? name, string? contactDetails, int? creditTermsDays, DateTimeOffset updatedAtUtc)
    {
        var validName = ValidName(name);
        var validContactDetails = ValidContactDetails(contactDetails);
        var validCreditTerms = ValidCreditTerms(creditTermsDays);
        var validUpdatedAtUtc = RequireUtc(updatedAtUtc);

        Name = validName;
        ContactDetails = validContactDetails;
        CreditTermsDays = validCreditTerms;
        UpdatedAtUtc = validUpdatedAtUtc;
    }

    /// <summary>Takes the customer out of use without deleting it. Deactivating an inactive customer changes nothing.</summary>
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
            throw new DomainException("customer.name_required", "The customer name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("customer.name_too_long", $"The customer name can have at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? ValidContactDetails(string? contactDetails)
    {
        var trimmed = string.IsNullOrWhiteSpace(contactDetails) ? null : contactDetails.Trim();

        if (trimmed is { Length: > MaxContactDetailsLength })
        {
            throw new DomainException("customer.contact_details_too_long", $"The contact details can have at most {MaxContactDetailsLength} characters.");
        }

        return trimmed;
    }

    private static int? ValidCreditTerms(int? creditTermsDays)
    {
        if (creditTermsDays is < 0)
        {
            throw new DomainException("customer.credit_terms_negative", "The credit terms cannot be negative.");
        }

        if (creditTermsDays is > MaxCreditTermsDays)
        {
            throw new DomainException("customer.credit_terms_too_long", $"The credit terms can be at most {MaxCreditTermsDays} days.");
        }

        return creditTermsDays;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException("customer.timestamp_not_utc", "Timestamps must be in UTC.");
        }

        return value;
    }
}
