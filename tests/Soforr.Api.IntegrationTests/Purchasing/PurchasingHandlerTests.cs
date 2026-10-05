using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Organizations;
using Soforr.Application.Purchasing;
using Soforr.Application.Purchasing.CreatePurchaseOrder;
using Soforr.Application.Purchasing.ReceivePurchase;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Domain.Purchasing;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Purchasing;

/// <summary>
/// The receiving use case and order creation on real PostgreSQL with the races forced deterministically: a competing
/// writer that commits between this request's read and its save (ADR 0004), and an organization that cannot be resolved.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PurchasingHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    private async Task<Guid> NewProductAsync(Guid organization)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>A placed order for 10 units of a new product; returns the order, its single line and the product.</summary>
    private async Task<(Guid Order, Guid Line, Guid Product)> PlacedOrderAsync(Guid organization)
    {
        var product = await NewProductAsync(organization);
        await using var db = database.CreateContext(organization);
        var supplier = Supplier.Create(Guid.CreateVersion7(), organization, "Gambia Grain", null, Now);
        db.Set<Supplier>().Add(supplier);
        var order = PurchaseOrder.CreateDraft(Guid.CreateVersion7(), organization, supplier.Id, Gmd, null, Now);
        var line = order.AddLine(Guid.CreateVersion7(), product, "SKU", "Rice", "bag", Quantity.Create(10m), Money.Create(2m, Gmd), Now);
        order.Place(Now);
        db.Set<PurchaseOrder>().Add(order);
        await db.SaveChangesAsync();
        return (order.Id, line.Id, product);
    }

    private static ReceivePurchaseHandler ReceiveHandler(AppDbContext db, FakeUser user) =>
        new(db, new StockMovementRecorder(db, user, new FixedClock(), new NewIds()), new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db));

    private async Task<Result<ReceiptResultDto>> ReceiveAsync(Guid organization, Guid order, Guid line, decimal quantity, Guid? receiptId, SaveChangesInterceptor? interceptor = null, Role role = Role.Owner)
    {
        var user = FakeUser.In(organization, role);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        await using var db = new AppDbContext(options.Options, user);
        return await ReceiveHandler(db, user).HandleAsync(new ReceivePurchaseCommand(order, receiptId, [new ReceivePurchaseLine(line, quantity)]), CancellationToken.None);
    }

    private async Task<decimal> StockAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return (await db.Set<StockLevel>().SingleOrDefaultAsync(level => level.ProductId == product))?.Quantity.Value ?? 0m;
    }

    private async Task<(decimal Received, int Movements, int Audits)> StateAsync(Guid organization, Guid order, Guid product)
    {
        await using var db = database.CreateContext(organization);
        var received = (await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order)).Lines.Sum(l => l.ReceivedQuantity.Value);
        return (
            received,
            await db.Set<InventoryMovement>().CountAsync(m => m.ProductId == product),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.PurchaseReceived));
    }

    [Fact]
    public async Task ARequestWhoseOrganizationCannotBeResolved_CreatesNothing()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var ghost = Guid.CreateVersion7(); // no such organization
        var user = FakeUser.In(ghost, Role.Owner);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);
        var handler = new CreatePurchaseOrderHandler(db, new FixedClock(), new NewIds(), user, new UnitOfWorkAuditLog(db));

        var result = await handler.HandleAsync(new CreatePurchaseOrderCommand(Guid.CreateVersion7(), null, [new CreatePurchaseOrderLine(product, 1m, 1m)]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("organization.not_found", result.Error!.Code);
        await using var check = database.CreateContext(organization);
        Assert.Equal(0, await check.Set<PurchaseOrder>().CountAsync());
        Assert.Equal(0, await check.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.PurchaseOrderCreated));
    }

    [Fact]
    public async Task ACompetingIdenticalReceiptCommittingFirst_IsRecognized_SoTheStockIsAddedOnce()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var receiptId = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await ReceiveAsync(organization, order, line, 4m, receiptId);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await ReceiveAsync(organization, order, line, 4m, receiptId, competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value.AlreadyRecorded);
        Assert.Equal(4m, result.Value.Order.Lines[0].ReceivedQuantity);
        Assert.Equal((4m, 1, 1), await StateAsync(organization, order, product));
        Assert.Equal(4m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingDifferentReceiptOnTheSameOrder_IsRetried_AndBothAreRecorded()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await ReceiveAsync(organization, order, line, 3m, Guid.CreateVersion7());
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await ReceiveAsync(organization, order, line, 4m, Guid.CreateVersion7(), competitor);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal(7m, result.Value.Order.Lines[0].ReceivedQuantity);
        Assert.Equal((7m, 2, 2), await StateAsync(organization, order, product));
        Assert.Equal(7m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ARetryAfterACompetingReceipt_RevalidatesAgainstWhatIsNowOutstanding()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await ReceiveAsync(organization, order, line, 8m, Guid.CreateVersion7());
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        // 5 fit when the request read the order (10 outstanding) but not after the competitor received 8.
        var result = await ReceiveAsync(organization, order, line, 5m, Guid.CreateVersion7(), competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("purchase_order.receipt_exceeds_ordered", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal((8m, 1, 1), await StateAsync(organization, order, product));
        Assert.Equal(8m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingCancel_BeforeTheSave_RefusesTheReceipt_AndAddsNoStock()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            await using var db = database.CreateContext(organization);
            var loaded = await db.Set<PurchaseOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == order);
            loaded.Cancel(Now.AddMinutes(1));
            await db.SaveChangesAsync();
        });

        var result = await ReceiveAsync(organization, order, line, 4m, Guid.CreateVersion7(), competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("purchase_order.not_receivable", result.Error!.Code);
        Assert.Equal((0m, 0, 0), await StateAsync(organization, order, product));
        Assert.Equal(0m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task AReceiptThatFailsToSave_LeavesTheOrderTheStockTheMovementsAndTheAuditUntouched()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var failing = new OnceBeforeSaveInterceptor(() => throw new InvalidOperationException("simulated failure before the save"));
        var user = FakeUser.In(organization, Role.Owner);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(failing);
        await using (var db = new AppDbContext(options.Options, user))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ReceiveHandler(db, user).HandleAsync(new ReceivePurchaseCommand(order, null, [new ReceivePurchaseLine(line, 4m)]), CancellationToken.None));
        }

        Assert.Equal((0m, 0, 0), await StateAsync(organization, order, product));
        Assert.Equal(0m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ACompetingReceiptWithTheSameIdAndADifferentQuantity_WinsOnce_AndOursIsAConflict()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var receiptId = Guid.CreateVersion7();
        var competitor = new OnceBeforeSaveInterceptor(async () =>
        {
            var won = await ReceiveAsync(organization, order, line, 4m, receiptId);
            Assert.True(won.IsSuccess, won.Error?.Code);
        });

        var result = await ReceiveAsync(organization, order, line, 6m, receiptId, competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("purchase_order.receipt_id_conflict", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal((4m, 1, 1), await StateAsync(organization, order, product));
        Assert.Equal(4m, await StockAsync(organization, product));
        await using var db = database.CreateContext(organization);
        Assert.Equal(1, await db.Set<PurchaseReceipt>().CountAsync());
    }

    [Fact]
    public async Task AReceiptThatFailedToSave_DoesNotReserveItsId_SoTheSameIdCanBeUsedAgain()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var receiptId = Guid.CreateVersion7();
        var failing = new OnceBeforeSaveInterceptor(() => throw new InvalidOperationException("simulated failure before the save"));
        var user = FakeUser.In(organization, Role.Owner);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(failing);
        await using (var db = new AppDbContext(options.Options, user))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ReceiveHandler(db, user).HandleAsync(new ReceivePurchaseCommand(order, receiptId, [new ReceivePurchaseLine(line, 4m)]), CancellationToken.None));
        }

        await using (var check = database.CreateContext(organization))
        {
            Assert.Equal(0, await check.Set<PurchaseReceipt>().CountAsync());
        }

        // Even a different quantity is fine: nothing committed under this id.
        var result = await ReceiveAsync(organization, order, line, 6m, receiptId);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal((6m, 1, 1), await StateAsync(organization, order, product));
        Assert.Equal(6m, await StockAsync(organization, product));
    }

    [Fact]
    public async Task ASuccessfulReceipt_StoresOneReceiptWithItsLines_InTheSameTransactionAsTheMovement()
    {
        var organization = await NewOrganizationAsync();
        var (order, line, product) = await PlacedOrderAsync(organization);
        var receiptId = Guid.CreateVersion7();

        var result = await ReceiveAsync(organization, order, line, 4m, receiptId);

        Assert.True(result.IsSuccess, result.Error?.Code);
        await using var db = database.CreateContext(organization);
        var stored = await db.Set<PurchaseReceipt>().Include(r => r.Lines).SingleAsync();
        Assert.Equal(receiptId, stored.Id);
        Assert.Equal(order, stored.PurchaseOrderId);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal(line, storedLine.LineId);
        Assert.Equal(product, storedLine.ProductId);
        Assert.Equal(4m, storedLine.Quantity.Value);
        Assert.Equal(receiptId, (await db.Set<InventoryMovement>().SingleAsync(m => m.ProductId == product)).SourceId);
    }

    [Fact]
    public async Task TheReceiptIdOfAnotherOrganization_IsNotSeen()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        var (firstOrder, firstLine, firstProduct) = await PlacedOrderAsync(first);
        var (secondOrder, secondLine, secondProduct) = await PlacedOrderAsync(second);
        var receiptId = Guid.CreateVersion7();
        Assert.True((await ReceiveAsync(first, firstOrder, firstLine, 2m, receiptId)).IsSuccess);

        var result = await ReceiveAsync(second, secondOrder, secondLine, 3m, receiptId);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal(2m, await StockAsync(first, firstProduct));
        Assert.Equal(3m, await StockAsync(second, secondProduct));
    }
}
