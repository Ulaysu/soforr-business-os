using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Inventory.AdjustInventory;
using Soforr.Application.Inventory.GetMovementHistory;
using Soforr.Application.Inventory.GetStockLevel;
using Soforr.Application.Inventory.ListLowStock;
using Soforr.Application.Organizations;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Inventory;

/// <summary>
/// The inventory use cases on real PostgreSQL (ADR 0004): the adjustment (permission, validation, idempotency, the audit
/// event in the same transaction as the movement, the stock rules) and the three queries (stock level, low stock,
/// movement history), each per role and scoped to the caller's organization.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InventoryHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
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

    /// <summary>Adds the event to the unit of work like the real log.</summary>
    private sealed class UnitOfWorkAuditLog(AppDbContext db) : IAuditLog
    {
        public void Record(AuditEntry entry) =>
            db.Set<AuditEvent>().Add(AuditEvent.Record(Guid.CreateVersion7(), entry.OrganizationId, entry.ActorId, entry.Action, entry.EntityType, entry.EntityId, entry.Description, "test-corr", Now));
    }

    private sealed class BeforeSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await action();
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

    private async Task<Guid> NewProductAsync(Guid organization, string name = "Rice", decimal? reorderPoint = null, bool active = true)
    {
        var settings = reorderPoint is { } point ? ReorderSettings.Create(Quantity.Create(point), 7) : null;
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), name, "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), settings, Now);
        if (!active)
        {
            product.Deactivate(Now);
        }

        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task ReceiveAsync(Guid organization, Guid product, decimal quantity)
    {
        await using var db = database.CreateContext(organization);
        var user = FakeUser.In(organization, Role.Owner);
        var recorder = new StockMovementRecorder(db, user, new FixedClock(), new NewIds());
        var result = await recorder.RecordAsync(
            new RecordMovementCommand(product, quantity, InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), Guid.CreateVersion7(), null),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    private (AppDbContext Db, FakeUser User, AdjustInventoryHandler Handler) StartAdjust(Guid organization, Role role = Role.Owner, SaveChangesInterceptor? interceptor = null)
    {
        var user = FakeUser.In(organization, role);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        var db = new AppDbContext(options.Options, user);
        var handler = new AdjustInventoryHandler(new StockMovementRecorder(db, user, new FixedClock(), new NewIds()), new UnitOfWorkAuditLog(db), user, new NewIds());
        return (db, user, handler);
    }

    private async Task<Result<AdjustmentResultDto>> AdjustAsync(Guid organization, AdjustInventoryCommand command, Role role = Role.Owner, SaveChangesInterceptor? interceptor = null)
    {
        var (db, _, handler) = StartAdjust(organization, role, interceptor);
        await using (db)
        {
            return await handler.HandleAsync(command, CancellationToken.None);
        }
    }

    private static AdjustInventoryCommand Adjust(Guid product, decimal? change, string? note = "Counted the shelf", Guid? id = null) => new(product, change, note, id);

    private async Task<decimal> LevelAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return (await db.Set<StockLevel>().SingleOrDefaultAsync(level => level.ProductId == product))?.Quantity.Value ?? 0m;
    }

    private async Task<InventoryMovement[]> MovementsAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<InventoryMovement>().Where(m => m.ProductId == product).OrderBy(m => m.OccurredAtUtc).ToArrayAsync();
    }

    private async Task<AuditEvent[]> AuditAsync(Guid organization, string action)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<AuditEvent>().Where(e => e.Action == action).ToArrayAsync();
    }

    // ---- AdjustInventory

    [Fact]
    public async Task AnAdjustment_RecordsAMovementUpdatesTheStock_AndWritesAnAuditEvent()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await ReceiveAsync(organization, product, 10m);
        var (db, user, handler) = StartAdjust(organization);

        Result<AdjustmentResultDto> result;
        await using (db)
        {
            result = await handler.HandleAsync(Adjust(product, -3.5m, "  Found damaged bags  "), CancellationToken.None);
        }

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal(6.5m, result.Value.NewQuantity);
        Assert.Equal(6.5m, await LevelAsync(organization, product));

        var movement = Assert.Single(await MovementsAsync(organization, product), m => m.Reason == InventoryMovementReason.ManualAdjustment);
        Assert.Equal(result.Value.MovementId, movement.Id);
        Assert.Equal(-3.5m, movement.QuantityDelta.Value);
        Assert.Equal(InventorySourceType.Adjustment, movement.SourceType);
        Assert.Equal(result.Value.AdjustmentId, movement.SourceId);
        Assert.Equal(user.UserId, movement.ActorId);
        Assert.Equal("Found damaged bags", movement.Note);

        var audit = Assert.Single(await AuditAsync(organization, "inventory.stock_adjusted"));
        Assert.Equal(user.UserId, audit.ActorId);
        Assert.Equal("InventoryMovement", audit.EntityType);
        Assert.Equal(movement.Id, audit.EntityId);
        Assert.Equal("Stock adjusted by -3.5.", audit.Description);
        Assert.DoesNotContain("damaged", audit.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APositiveAdjustment_AddsStock_ToAProductThatNeverHadAny()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await AdjustAsync(organization, Adjust(product, 4m, "Opening count"));

        Assert.Equal(4m, result.Value.NewQuantity);
        Assert.Equal(4m, await LevelAsync(organization, product));
        Assert.Single(await AuditAsync(organization, "inventory.stock_adjusted"));
    }

    [Fact]
    public async Task AnAdjustmentOfAnInactiveProduct_IsAllowed()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, active: false);

        var result = await AdjustAsync(organization, Adjust(product, 2m));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RemovingMoreThanThereIs_IsARuleViolation_AndRecordsNeitherAMovementNorAnAuditEvent()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await ReceiveAsync(organization, product, 2m);

        var result = await AdjustAsync(organization, Adjust(product, -2.0001m));

        Assert.Equal("stock_level.insufficient_stock", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal(2m, await LevelAsync(organization, product));
        Assert.Single(await MovementsAsync(organization, product));
        Assert.Empty(await AuditAsync(organization, "inventory.stock_adjusted"));
    }

    [Theory]
    [InlineData(null, "Counted", "inventory.quantity_change_required", "quantityChange")]
    [InlineData("0", "Counted", "quantity_delta.zero", "quantityChange")]
    [InlineData("1.00001", "Counted", "quantity_delta.scale_exceeded", "quantityChange")]
    [InlineData("1", null, "inventory_movement.note_required", "note")]
    [InlineData("1", "", "inventory_movement.note_required", "note")]
    [InlineData("1", "   ", "inventory_movement.note_required", "note")]
    public async Task InvalidAdjustments_AreValidationErrors_NamingTheField_AndStoreNothing(string? change, string? note, string code, string field)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        decimal? parsed = change is null ? null : decimal.Parse(change, System.Globalization.CultureInfo.InvariantCulture);

        var result = await AdjustAsync(organization, Adjust(product, parsed, note));

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(field, result.Error.Field);
        Assert.Empty(await MovementsAsync(organization, product));
        Assert.Empty(await AuditAsync(organization, "inventory.stock_adjusted"));
    }

    [Fact]
    public async Task ANoteThatIsTooLong_IsAValidationError()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await AdjustAsync(organization, Adjust(product, 1m, new string('n', InventoryMovement.MaxNoteLength + 1)));

        Assert.Equal("inventory_movement.note_too_long", result.Error!.Code);
        Assert.Equal("note", result.Error.Field);
    }

    [Fact]
    public async Task AnEmptyAdjustmentId_IsAValidationError()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await AdjustAsync(organization, Adjust(product, 1m, id: Guid.Empty));

        Assert.Equal("inventory.adjustment_id_invalid", result.Error!.Code);
        Assert.Equal("adjustmentId", result.Error.Field);
    }

    [Fact]
    public async Task RepeatingAnAdjustmentWithTheSameId_ChangesNothing_AndAddsNoSecondAuditEvent()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var id = Guid.CreateVersion7();
        var first = await AdjustAsync(organization, Adjust(product, 5m, id: id));

        var again = await AdjustAsync(organization, Adjust(product, 5m, id: id));

        Assert.False(first.Value.AlreadyRecorded);
        Assert.True(again.IsSuccess);
        Assert.True(again.Value.AlreadyRecorded);
        Assert.Equal(first.Value.MovementId, again.Value.MovementId);
        Assert.Equal(5m, again.Value.NewQuantity);
        Assert.Equal(5m, await LevelAsync(organization, product));
        Assert.Single(await MovementsAsync(organization, product));
        Assert.Single(await AuditAsync(organization, "inventory.stock_adjusted"));
    }

    [Fact]
    public async Task WithoutAnAdjustmentId_EachAdjustmentIsNew()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        await AdjustAsync(organization, Adjust(product, 1m));
        await AdjustAsync(organization, Adjust(product, 1m));

        Assert.Equal(2m, await LevelAsync(organization, product));
        Assert.Equal(2, (await AuditAsync(organization, "inventory.stock_adjusted")).Length);
    }

    [Fact]
    public async Task TheAuditEventIsInTheSameTransaction_SoAFailedSaveRecordsNeitherTheMovementNorTheEvent()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var removeTheProduct = new BeforeSaveInterceptor(async () =>
        {
            await using var admin = database.CreateContext();
            await admin.Database.ExecuteSqlRawAsync("DELETE FROM \"Products\" WHERE \"Id\" = {0}", product);
        });
        var (db, _, handler) = StartAdjust(organization, interceptor: removeTheProduct);

        await using (db)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => handler.HandleAsync(Adjust(product, 1m), CancellationToken.None));
        }

        await using var verify = database.CreateContext(organization);
        Assert.Equal(0, await verify.Set<InventoryMovement>().CountAsync());
        Assert.Equal(0, await verify.Set<AuditEvent>().CountAsync(e => e.Action == "inventory.stock_adjusted"));
    }

    [Fact]
    public async Task AfterAConflictAndARetry_ThereIsExactlyOneMovementAndOneAuditEvent()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await ReceiveAsync(organization, product, 10m);
        var interceptions = 0;
        var competitor = new BeforeSaveInterceptor(async () =>
        {
            if (interceptions++ == 0)
            {
                await AdjustAsync(organization, Adjust(product, -1m, "Competing adjustment"));
            }
        });

        var result = await AdjustAsync(organization, Adjust(product, -2m, "Mine"), interceptor: competitor);

        Assert.True(result.IsSuccess);
        Assert.Equal(7m, result.Value.NewQuantity);
        Assert.Equal(7m, await LevelAsync(organization, product));
        Assert.Equal(2, (await AuditAsync(organization, "inventory.stock_adjusted")).Length);
        Assert.Equal(2, (await MovementsAsync(organization, product)).Count(m => m.Reason == InventoryMovementReason.ManualAdjustment));
        Assert.DoesNotContain(product, await InventoryPersistenceTests.ReconciliationMismatchesAsync(database.ConnectionString));
    }

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task OnlyOwnerAndManager_MayAdjustStock(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await AdjustAsync(organization, Adjust(product, 1m), role);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("identity.forbidden", result.Error!.Code);
            Assert.Empty(await MovementsAsync(organization, product));
            Assert.Empty(await AuditAsync(organization, "inventory.stock_adjusted"));
        }
    }

    [Fact]
    public async Task WithoutBeingSignedIn_AnAdjustmentIsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var anonymous = new FakeUser(null, null, null);
        await using var db = database.CreateContext();
        var handler = new AdjustInventoryHandler(new StockMovementRecorder(db, anonymous, new FixedClock(), new NewIds()), new UnitOfWorkAuditLog(db), anonymous, new NewIds());

        var result = await handler.HandleAsync(Adjust(product, 1m), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    [Fact]
    public async Task AnUnknownOrForeignProduct_IsNotFound_AndNothingIsRecordedForTheOtherOrganization()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await NewProductAsync(theirs);

        var unknown = await AdjustAsync(mine, Adjust(Guid.CreateVersion7(), 1m));
        var foreign = await AdjustAsync(mine, Adjust(theirProduct, 1m));

        Assert.Equal("product.not_found", unknown.Error!.Code);
        Assert.Equal("product.not_found", foreign.Error!.Code);
        Assert.Empty(await MovementsAsync(theirs, theirProduct));
        Assert.Empty(await AuditAsync(theirs, "inventory.stock_adjusted"));
    }

    // ---- GetStockLevel

    private async Task<Result<StockLevelDto>> StockAsync(Guid organization, Guid product, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new GetStockLevelHandler(db, FakeUser.In(organization, role)).HandleAsync(new GetStockLevelQuery(product), CancellationToken.None);
    }

    [Fact]
    public async Task TheStockLevel_ShowsTheQuantityAndTheReorderSettings()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, reorderPoint: 5m);
        await ReceiveAsync(organization, product, 8m);

        var result = await StockAsync(organization, product);

        Assert.Equal(8m, result.Value.Quantity);
        Assert.Equal(5m, result.Value.ReorderPoint);
        Assert.Equal(7, result.Value.ReorderLeadTimeDays);
        Assert.False(result.Value.IsLow);
        Assert.NotNull(result.Value.UpdatedAtUtc);
    }

    [Fact]
    public async Task AProductThatNeverHadStock_HasQuantityZero_AndNoUpdateTime()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization, reorderPoint: 5m);

        var result = await StockAsync(organization, product);

        Assert.Equal(0m, result.Value.Quantity);
        Assert.True(result.Value.IsLow);
        Assert.Null(result.Value.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Manager)]
    [InlineData(Role.Staff)]
    public async Task EveryRole_MayViewStock(Role role)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        Assert.True((await StockAsync(organization, product, role)).IsSuccess);
    }

    [Fact]
    public async Task AForeignOrUnknownProduct_HasNoStockToSee()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await NewProductAsync(theirs);

        var foreign = await StockAsync(mine, theirProduct);
        var unknown = await StockAsync(mine, Guid.CreateVersion7());

        Assert.Equal("product.not_found", foreign.Error!.Code);
        Assert.Equal("product.not_found", unknown.Error!.Code);
    }

    [Fact]
    public async Task WithoutBeingSignedIn_TheStockIsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new GetStockLevelHandler(db, new FakeUser(null, null, null)).HandleAsync(new GetStockLevelQuery(Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    // ---- ListLowStock

    private async Task<Result<PagedResult<LowStockItemDto>>> LowAsync(Guid organization, int page = 1, int pageSize = 25, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new ListLowStockHandler(db, FakeUser.In(organization, role)).HandleAsync(new ListLowStockQuery(page, pageSize), CancellationToken.None);
    }

    [Fact]
    public async Task LowStock_IsAtOrBelowTheReorderPoint_ForActiveProductsWithReorderSettings()
    {
        var organization = await NewOrganizationAsync();
        var below = await NewProductAsync(organization, "Below", reorderPoint: 10m);
        var atPoint = await NewProductAsync(organization, "At point", reorderPoint: 10m);
        var above = await NewProductAsync(organization, "Above", reorderPoint: 10m);
        var noStock = await NewProductAsync(organization, "No stock ever", reorderPoint: 3m);
        var noSettings = await NewProductAsync(organization, "No settings");
        var inactive = await NewProductAsync(organization, "Inactive", reorderPoint: 10m, active: false);
        await ReceiveAsync(organization, below, 4m);
        await ReceiveAsync(organization, atPoint, 10m);
        await ReceiveAsync(organization, above, 10.0001m);
        await ReceiveAsync(organization, noSettings, 0.5m);
        await ReceiveAsync(organization, inactive, 1m);

        var result = await LowAsync(organization);

        Assert.Equal(["No stock ever", "Below", "At point"], result.Value.Items.Select(item => item.Name).ToArray());
        Assert.Equal(3, result.Value.TotalCount);
        var first = result.Value.Items[0];
        Assert.Equal(noStock, first.ProductId);
        Assert.Equal(0m, first.Quantity);
        Assert.Equal(3m, first.ReorderPoint);
        Assert.Equal(7, first.ReorderLeadTimeDays);
        Assert.DoesNotContain(result.Value.Items, item => item.ProductId == above || item.ProductId == noSettings || item.ProductId == inactive);
    }

    [Fact]
    public async Task AReorderPointOfZero_IsLowOnlyWhenThereIsNoStock()
    {
        var organization = await NewOrganizationAsync();
        var empty = await NewProductAsync(organization, "Empty", reorderPoint: 0m);
        var stocked = await NewProductAsync(organization, "Stocked", reorderPoint: 0m);
        await ReceiveAsync(organization, stocked, 0.0001m);

        var result = await LowAsync(organization);

        Assert.Equal([empty], result.Value.Items.Select(item => item.ProductId).ToArray());
    }

    [Fact]
    public async Task LowStock_IsOrderedEmptiestFirst_ThenByName_AndPaged()
    {
        var organization = await NewOrganizationAsync();
        var b = await NewProductAsync(organization, "B", reorderPoint: 10m);
        var a = await NewProductAsync(organization, "A", reorderPoint: 10m);
        var c = await NewProductAsync(organization, "C", reorderPoint: 10m);
        await ReceiveAsync(organization, b, 2m);
        await ReceiveAsync(organization, a, 2m);
        await ReceiveAsync(organization, c, 1m);

        var first = await LowAsync(organization, 1, 2);
        var second = await LowAsync(organization, 2, 2);

        Assert.Equal(["C", "A"], first.Value.Items.Select(item => item.Name).ToArray());
        Assert.Equal(["B"], second.Value.Items.Select(item => item.Name).ToArray());
        Assert.Equal(3, second.Value.TotalCount);
    }

    [Theory]
    [InlineData(0, 25, "pagination.page_invalid")]
    [InlineData(1, 0, "pagination.page_size_invalid")]
    [InlineData(1, 101, "pagination.page_size_invalid")]
    public async Task LowStockPaging_OutsideTheBounds_IsRejected(int page, int pageSize, string code)
    {
        var organization = await NewOrganizationAsync();

        var result = await LowAsync(organization, page, pageSize);

        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public async Task LowStock_ContainsOnlyTheCallersOrganization()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        await NewProductAsync(mine, "Mine", reorderPoint: 5m);
        await NewProductAsync(theirs, "Theirs", reorderPoint: 5m);

        var result = await LowAsync(mine);

        Assert.Equal(["Mine"], result.Value.Items.Select(item => item.Name).ToArray());
    }

    [Fact]
    public async Task TheLowStockList_AndTheStockLevel_AgreeOnWhatIsLow()
    {
        var organization = await NewOrganizationAsync();
        var products = new List<Guid>();
        foreach (var (name, point, stock) in new[] { ("P1", 5m, 5m), ("P2", 5m, 5.0001m), ("P3", 5m, 0m), ("P4", 0m, 0m) })
        {
            var id = await NewProductAsync(organization, name, reorderPoint: point);
            if (stock > 0m)
            {
                await ReceiveAsync(organization, id, stock);
            }

            products.Add(id);
        }

        var listed = (await LowAsync(organization)).Value.Items.Select(item => item.ProductId).ToHashSet();

        foreach (var id in products)
        {
            Assert.Equal(listed.Contains(id), (await StockAsync(organization, id)).Value.IsLow);
        }
    }

    // ---- GetMovementHistory

    private async Task<Result<PagedResult<MovementDto>>> HistoryAsync(Guid organization, Guid product, int page = 1, int pageSize = 25, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new GetMovementHistoryHandler(db, FakeUser.In(organization, role)).HandleAsync(new GetMovementHistoryQuery(product, page, pageSize), CancellationToken.None);
    }

    [Fact]
    public async Task TheHistory_ShowsEveryMovement_NewestFirst_WithItsReasonSourceActorAndNote()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await ReceiveAsync(organization, product, 10m);
        var adjusted = await AdjustAsync(organization, Adjust(product, -2m, "Shelf count"));

        var result = await HistoryAsync(organization, product);

        Assert.Equal(2, result.Value.TotalCount);
        var newest = result.Value.Items[0];
        Assert.Equal(adjusted.Value.MovementId, newest.Id);
        Assert.Equal(-2m, newest.QuantityChange);
        Assert.Equal("ManualAdjustment", newest.Reason);
        Assert.Equal("Adjustment", newest.SourceType);
        Assert.Equal("Shelf count", newest.Note);
        var oldest = result.Value.Items[1];
        Assert.Equal(10m, oldest.QuantityChange);
        Assert.Equal("PurchaseReceived", oldest.Reason);
        Assert.Equal("PurchaseReceipt", oldest.SourceType);
        Assert.Null(oldest.Note);
    }

    [Fact]
    public async Task TheHistory_IsPaged_AndOutOfRangePagingIsRejected()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        for (var index = 0; index < 5; index++)
        {
            await ReceiveAsync(organization, product, 1m);
        }

        var second = await HistoryAsync(organization, product, 2, 2);
        var beyond = await HistoryAsync(organization, product, 4, 2);
        var bad = await HistoryAsync(organization, product, 1, 101);

        Assert.Equal(2, second.Value.Items.Count);
        Assert.Equal(5, second.Value.TotalCount);
        Assert.Empty(beyond.Value.Items);
        Assert.Equal("pagination.page_size_invalid", bad.Error!.Code);
    }

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task OnlyOwnerAndManager_MayReadTheHistory(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await HistoryAsync(organization, product, role: role);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("identity.forbidden", result.Error!.Code);
        }
    }

    [Fact]
    public async Task TheHistoryOfAForeignOrUnknownProduct_IsNotFound_AndNeverShowsAnotherOrganizationsMovements()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await NewProductAsync(theirs);
        await ReceiveAsync(theirs, theirProduct, 5m);

        var foreign = await HistoryAsync(mine, theirProduct);
        var unknown = await HistoryAsync(mine, Guid.CreateVersion7());

        Assert.Equal("product.not_found", foreign.Error!.Code);
        Assert.Equal("product.not_found", unknown.Error!.Code);
    }

    [Fact]
    public async Task WithoutBeingSignedIn_TheHistoryIsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new GetMovementHistoryHandler(db, new FakeUser(null, null, null)).HandleAsync(new GetMovementHistoryQuery(Guid.CreateVersion7(), 1, 25), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }
}
