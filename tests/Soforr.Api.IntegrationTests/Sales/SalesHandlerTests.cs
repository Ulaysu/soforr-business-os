using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Organizations;
using Soforr.Application.Sales;
using Soforr.Application.Sales.CreateSale;
using Soforr.Application.Sales.VoidSale;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Sales;

/// <summary>
/// Selling and voiding on real PostgreSQL with the races forced deterministically: a competing writer that commits
/// between this request's read and its save (ADR 0004), a save that fails, and an organization that cannot be resolved.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SalesHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class NewIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private sealed class UnitOfWorkAuditLog(AppDbContext db) : IAuditLog
    {
        public void Record(AuditEntry entry) =>
            db.Set<AuditEvent>().Add(AuditEvent.Record(Guid.CreateVersion7(), entry.OrganizationId, entry.ActorId, entry.Action, entry.EntityType, entry.EntityId, entry.Description, "test-corr", Now));
    }

    /// <summary>Runs <paramref name="action"/> once, just before the first save of the request under test.</summary>
    private sealed class OnceBeforeSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        private int _ran;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                await action();
            }

            return result;
        }
    }

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Gmd, "UTC", Now));
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>A product priced 10 (cost 6) with the given stock, recorded through the stock recorder.</summary>
    private async Task<Guid> NewProductAsync(Guid organization, decimal stock)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(6m, Gmd), null, Now);
        await using (var db = database.CreateContext(organization))
        {
            db.Set<Product>().Add(product);
            await db.SaveChangesAsync();
        }

        if (stock > 0m)
        {
            await using var db = database.CreateContext(organization);
            var user = FakeUser.In(organization, Role.Owner);
            var recorded = await new StockMovementRecorder(db, user, new FixedClock(), new NewIds()).RecordAsync(
                new RecordMovementCommand(product.Id, stock, InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), user.UserId!.Value, null),
                CancellationToken.None);
            Assert.True(recorded.IsSuccess, recorded.Error?.Code);
        }

        return product.Id;
    }

    private static AppDbContext NewContext(string connectionString, FakeUser user, SaveChangesInterceptor? interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AppDbContext(options.Options, user);
    }

    private static CreateSaleHandler SaleHandler(AppDbContext db, FakeUser user) =>
        new(db, new StockMovementRecorder(db, user, new FixedClock(), new NewIds()), new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db));

    private static VoidSaleHandler VoidHandler(AppDbContext db, FakeUser user) =>
        new(db, new StockMovementRecorder(db, user, new FixedClock(), new NewIds()), new FixedClock(), user, new UnitOfWorkAuditLog(db));

    private async Task<Result<SaleResultDto>> SellAsync(Guid organization, Guid product, decimal quantity, Guid? saleId, SaveChangesInterceptor? interceptor = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        await using var db = NewContext(database.ConnectionString, user, interceptor);
        return await SaleHandler(db, user).HandleAsync(new CreateSaleCommand(saleId, [new CreateSaleLine(product, quantity)]), CancellationToken.None);
    }

    private async Task<Result<SaleDto>> VoidAsync(Guid organization, Guid sale, SaveChangesInterceptor? interceptor = null)
    {
        var user = FakeUser.In(organization, Role.Owner);
        await using var db = NewContext(database.ConnectionString, user, interceptor);
        return await VoidHandler(db, user).HandleAsync(new VoidSaleCommand(sale, "Test void"), CancellationToken.None);
    }

    private async Task<decimal> StockAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return (await db.Set<StockLevel>().SingleOrDefaultAsync(level => level.ProductId == product))?.Quantity.Value ?? 0m;
    }

    private async Task<(int Sales, int Sold, int Returned, int Created, int Voided)> StateAsync(Guid organization)
    {
        await using var db = database.CreateContext(organization);
        return (
            await db.Set<Sale>().CountAsync(),
            await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted),
            await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.Return),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.SaleCreated),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.SaleVoided));
    }

    [Fact]
    public async Task ARequestWhoseOrganizationCannotBeResolved_CreatesNothing()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 5m);
        var user = FakeUser.In(Guid.CreateVersion7(), Role.Owner); // no such organization
        await using var db = NewContext(database.ConnectionString, user, interceptor: null);

        var result = await SaleHandler(db, user).HandleAsync(new CreateSaleCommand(null, [new CreateSaleLine(product, 1m)]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("organization.not_found", result.Error!.Code);
        Assert.Equal((0, 0, 0, 0, 0), await StateAsync(organization));
        Assert.Equal(5m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ASaleThatFailsToSave_LeavesTheStockTheMovementsAndTheAuditUntouched_AndDoesNotReserveItsId()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var saleId = Guid.CreateVersion7();
        var user = FakeUser.In(organization, Role.Owner);
        await using (var db = NewContext(database.ConnectionString, user, new OnceBeforeSaveInterceptor(() => throw new InvalidOperationException("simulated failure before the save"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SaleHandler(db, user).HandleAsync(new CreateSaleCommand(saleId, [new CreateSaleLine(product, 4m)]), CancellationToken.None));
        }

        Assert.Equal((0, 0, 0, 0, 0), await StateAsync(organization));
        Assert.Equal(10m, await StockAsync(organization, product));

        // The same id with a different quantity is fine: nothing committed under it.
        var result = await SellAsync(organization, product, 6m, saleId);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal((1, 1, 0, 1, 0), await StateAsync(organization));
        Assert.Equal(4m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingIdenticalSaleCommittingFirst_IsRecognized_SoTheStockIsRemovedOnce()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var saleId = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await SellAsync(organization, product, 4m, saleId);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await SellAsync(organization, product, 4m, saleId, competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value.AlreadyRecorded);
        Assert.Equal((1, 1, 0, 1, 0), await StateAsync(organization));
        Assert.Equal(6m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingSaleWithTheSameIdAndADifferentQuantity_WinsOnce_AndOursIsAConflict()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var saleId = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await SellAsync(organization, product, 4m, saleId);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await SellAsync(organization, product, 6m, saleId, competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("sale.id_conflict", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal((1, 1, 0, 1, 0), await StateAsync(organization));
        Assert.Equal(6m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingSaleOfTheSameStock_IsRetried_AndRevalidatedAgainstWhatIsLeft()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await SellAsync(organization, product, 8m, null);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        // 5 fit when the request read the stock (10) but not after the competitor took 8.
        var result = await SellAsync(organization, product, 5m, null, competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("stock_level.insufficient_stock", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal((1, 1, 0, 1, 0), await StateAsync(organization));
        Assert.Equal(2m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingSaleThatLeavesEnoughStock_IsRetried_AndBothSucceed()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await SellAsync(organization, product, 3m, null);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await SellAsync(organization, product, 4m, null, competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal((2, 2, 0, 2, 0), await StateAsync(organization));
        Assert.Equal(3m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingVoidCommittingFirst_MeansOursIsRefused_AndTheStockIsReturnedOnce()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var sold = await SellAsync(organization, product, 6m, null);
        Assert.True(sold.IsSuccess, sold.Error?.Code);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await VoidAsync(organization, sold.Value.SaleId);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await VoidAsync(organization, sold.Value.SaleId, competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("sale.not_voidable", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal((1, 1, 1, 1, 1), await StateAsync(organization));
        Assert.Equal(10m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task AVoidThatFailsToSave_LeavesTheSaleAndTheStockUntouched_AndCanBeRepeated()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, 10m);
        var sold = await SellAsync(organization, product, 6m, null);
        Assert.True(sold.IsSuccess, sold.Error?.Code);
        var user = FakeUser.In(organization, Role.Owner);
        await using (var db = NewContext(database.ConnectionString, user, new OnceBeforeSaveInterceptor(() => throw new InvalidOperationException("simulated failure before the save"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                VoidHandler(db, user).HandleAsync(new VoidSaleCommand(sold.Value.SaleId, "Reason"), CancellationToken.None));
        }

        Assert.Equal((1, 1, 0, 1, 0), await StateAsync(organization));
        Assert.Equal(4m, await StockAsync(organization, product));

        var again = await VoidAsync(organization, sold.Value.SaleId);

        Assert.True(again.IsSuccess, again.Error?.Code);
        Assert.Equal((1, 1, 1, 1, 1), await StateAsync(organization));
        Assert.Equal(10m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task TheSaleIdOfAnotherOrganization_IsNotSeen()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var firstProduct = await NewProductAsync(first, 10m);
        var secondProduct = await NewProductAsync(second, 10m);
        var saleId = Guid.CreateVersion7();
        Assert.True((await SellAsync(first, firstProduct, 2m, saleId)).IsSuccess);

        var result = await SellAsync(second, secondProduct, 3m, saleId);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal(8m, await StockAsync(first, firstProduct));
        Assert.Equal(7m, await StockAsync(second, secondProduct));
    }
}
