using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Organizations;
using Soforr.Domain.Common;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;

namespace Soforr.Api.IntegrationTests.Organizations;

/// <summary>
/// The Organization module owns the organization's currency and every money-recording module obtains it through
/// <see cref="OrganizationCurrency"/> (a request never carries one). These tests prove the resolver answers with the caller's
/// own organization's currency only, fails closed, and that a purchase order built from it stores exactly that currency.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrganizationCurrencyTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 30, 15, TimeSpan.Zero);

    private async Task<Guid> NewOrganizationAsync(string currency)
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Org " + currency, Currency.Create(currency), "UTC", Now));
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ItAnswersWithTheCallersOwnOrganizationsCurrency()
    {
        var gambia = await NewOrganizationAsync("GMD");
        var america = await NewOrganizationAsync("USD");

        await using var asGambia = database.CreateContext(gambia);
        await using var asAmerica = database.CreateContext(america);

        Assert.Equal(Currency.Create("GMD"), (await OrganizationCurrency.ResolveAsync(asGambia, gambia, CancellationToken.None)).Value);
        Assert.Equal(Currency.Create("USD"), (await OrganizationCurrency.ResolveAsync(asAmerica, america, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task AnotherOrganizationsIdAnAbsentIdOrNoOrganization_FailClosedAsNotFound()
    {
        var mine = await NewOrganizationAsync("GMD");
        var theirs = await NewOrganizationAsync("USD");
        await using var asMine = database.CreateContext(mine);
        await using var nobody = database.CreateContext();

        var foreign = await OrganizationCurrency.ResolveAsync(asMine, theirs, CancellationToken.None);
        var unknown = await OrganizationCurrency.ResolveAsync(asMine, Guid.CreateVersion7(), CancellationToken.None);
        var absent = await OrganizationCurrency.ResolveAsync(asMine, null, CancellationToken.None);
        var noContext = await OrganizationCurrency.ResolveAsync(nobody, mine, CancellationToken.None);

        foreach (var result in new[] { foreign, unknown, absent, noContext })
        {
            Assert.True(result.IsFailure);
            Assert.Equal("organization.not_found", result.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        }
    }

    [Fact]
    public async Task APurchaseOrderBuiltFromTheResolvedCurrency_IsStoredInThatCurrency_AndCannotMixAnother()
    {
        var organization = await NewOrganizationAsync("GMD");
        var supplier = Supplier.Create(Guid.CreateVersion7(), organization, "Supplier", null, Now);
        await using (var seed = database.CreateContext(organization))
        {
            seed.Set<Supplier>().Add(supplier);
            await seed.SaveChangesAsync();
        }

        await using (var db = database.CreateContext(organization))
        {
            var currency = await OrganizationCurrency.ResolveAsync(db, organization, CancellationToken.None);
            db.Set<PurchaseOrder>().Add(PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier.Id, currency.Value, null, Now));
            await db.SaveChangesAsync();
        }

        await using var reader = database.CreateContext(organization);
        var stored = await reader.Set<PurchaseOrder>().AsNoTracking().SingleAsync();
        Assert.Equal(Currency.Create("GMD"), stored.Currency);

        // The Domain keeps every amount of the order in that one currency, whatever a caller tries to add.
        var exception = Assert.Throws<DomainException>(() => stored.AddLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "SKU", "Name", "bag", Quantity.Create(1), Money.Create(1m, Currency.Create("USD")), Now));
        Assert.Equal("purchase_order.line_currency_mismatch", exception.Code);
    }
}
