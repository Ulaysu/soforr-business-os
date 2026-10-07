using Soforr.Domain.Customers;

namespace Soforr.Application.Customers;

/// <summary>
/// A customer as it is returned to callers; the Domain entity is never exposed. <see cref="ContactDetails"/> is personal data
/// and is null for a caller who may not manage customers (a cashier needs the name and the terms, not the phone number).
/// </summary>
public sealed record CustomerDto(
    Guid Id,
    string Name,
    string? ContactDetails,
    int? CreditTermsDays,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal static CustomerDto From(Customer customer, bool includeContactDetails)
    {
        ArgumentNullException.ThrowIfNull(customer);

        return new CustomerDto(
            customer.Id,
            customer.Name,
            includeContactDetails ? customer.ContactDetails : null,
            customer.CreditTermsDays,
            customer.IsActive,
            customer.CreatedAtUtc,
            customer.UpdatedAtUtc);
    }
}
