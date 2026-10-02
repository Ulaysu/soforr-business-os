using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Organizations;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Inventory;

/// <summary>
/// The stock movement recorder on real PostgreSQL (ADR 0004): one transaction for the movement and the stock
/// level, optimistic concurrency with bounded retry that re-validates, idempotency, and the invariant that a stock
/// level always equals the sum of its movements. The races are made deterministic with a save interceptor that
/// performs the competing write just before the save, because ordinary concurrency cannot prove a path was reached;
/// real parallel tests then check the outcome.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StockMovementRecorderTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");
    private static readonly string[] ExpectedLosses = ["stock_level.insufficient_stock", "inventory.concurrency_conflict"];

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class NewIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeUser(Guid? organizationId) : ICurrentUser, IOrganizationContext
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = Domain.Identity.Role.Owner;
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

    private async Task<Guid> NewProductAsync(Guid organization)
    {
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(5m, Gmd), null, Now);
        await using var db = database.CreateContext(organization);
        db.Set<Product>().Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>A recorder on its own context (its own request), optionally with an interceptor on the context.</summary>
    private (AppDbContext Db, StockMovementRecorder Recorder) Start(Guid? organization, SaveChangesInterceptor? interceptor = null)
    {
        var user = new FakeUser(organization);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        var db = new AppDbContext(options.Options, user);
        return (db, new StockMovementRecorder(db, user, new FixedClock(), new NewIds()));
    }

    private async Task<Result<MovementResult>> RecordAsync(Guid organization, RecordMovementCommand command, SaveChangesInterceptor? interceptor = null)
    {
        var (db, recorder) = Start(organization, interceptor);
        await using (db)
        {
            return await recorder.RecordAsync(command, CancellationToken.None);
        }
    }

    private static RecordMovementCommand Receive(Guid product, decimal quantity, Guid? source = null) =>
        new(product, quantity, InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, source ?? Guid.CreateVersion7(), Guid.CreateVersion7(), null);

    private static RecordMovementCommand Sell(Guid product, decimal quantity, Guid? source = null) =>
        new(product, -quantity, InventoryMovementReason.SaleCompleted, InventorySourceType.Sale, source ?? Guid.CreateVersion7(), Guid.CreateVersion7(), null);

    private async Task<decimal> LevelAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return (await db.Set<StockLevel>().SingleOrDefaultAsync(level => level.ProductId == product))?.Quantity.Value ?? 0m;
    }

    private async Task<int> MovementCountAsync(Guid organization, Guid product)
    {
        await using var db = database.CreateContext(organization);
        return await db.Set<InventoryMovement>().CountAsync(movement => movement.ProductId == product);
    }

    private async Task AssertReconciledAsync(Guid product) =>
        Assert.DoesNotContain(product, await InventoryPersistenceTests.ReconciliationMismatchesAsync(database.ConnectionString));

    // ---- recording

    [Fact]
    public async Task AReceiptAndASale_UpdateTheStockLevel_AndAreRecordedAsMovements()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var received = await RecordAsync(organization, Receive(product, 10m));
        var sold = await RecordAsync(organization, Sell(product, 3.5m));

        Assert.Equal(10m, received.Value.NewQuantity);
        Assert.Equal(6.5m, sold.Value.NewQuantity);
        Assert.False(sold.Value.AlreadyRecorded);
        Assert.Equal(6.5m, await LevelAsync(organization, product));
        Assert.Equal(2, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task TheMovementCarriesItsReasonSourceActorAndTime()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var source = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();

        var result = await RecordAsync(organization, new RecordMovementCommand(product, 4m, InventoryMovementReason.ManualAdjustment, InventorySourceType.Adjustment, source, actor, " found 4 more "));

        await using var db = database.CreateContext(organization);
        var movement = await db.Set<InventoryMovement>().SingleAsync(m => m.Id == result.Value.MovementId);
        Assert.Equal(InventoryMovementReason.ManualAdjustment, movement.Reason);
        Assert.Equal(InventorySourceType.Adjustment, movement.SourceType);
        Assert.Equal(source, movement.SourceId);
        Assert.Equal(actor, movement.ActorId);
        Assert.Equal("found 4 more", movement.Note);
        Assert.Equal(Now, movement.OccurredAtUtc);
        Assert.Equal(organization, movement.OrganizationId);
    }

    [Fact]
    public async Task SellingMoreThanThereIs_IsARuleViolation_AndChangesNothing()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 2m));

        var result = await RecordAsync(organization, Sell(product, 2.0001m));

        Assert.Equal("stock_level.insufficient_stock", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal(2m, await LevelAsync(organization, product));
        Assert.Equal(1, await MovementCountAsync(organization, product));
    }

    [Fact]
    public async Task SellingTheLastUnits_ExactlyEmptiesTheStock_AndNeverGoesNegative()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 2m));

        var emptied = await RecordAsync(organization, Sell(product, 2m));
        var more = await RecordAsync(organization, Sell(product, 0.0001m));

        Assert.True(emptied.IsSuccess);
        Assert.Equal(0m, await LevelAsync(organization, product));
        Assert.Equal("stock_level.insufficient_stock", more.Error!.Code);
    }

    [Fact]
    public async Task SellingAProductThatHasNeverHadStock_IsInsufficientStock_AndOpensNoLevel()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await RecordAsync(organization, Sell(product, 1m));

        Assert.Equal("stock_level.insufficient_stock", result.Error!.Code);
        Assert.Equal(0, await MovementCountAsync(organization, product));
        await using var db = database.CreateContext(organization);
        Assert.Equal(0, await db.Set<StockLevel>().CountAsync());
    }

    [Theory]
    [InlineData(0, InventoryMovementReason.ManualAdjustment, "note", "quantity_delta.zero")]
    [InlineData(5, InventoryMovementReason.ManualAdjustment, null, "inventory_movement.note_required")]
    [InlineData(-5, InventoryMovementReason.PurchaseReceived, null, "inventory_movement.delta_must_be_positive")]
    [InlineData(5, InventoryMovementReason.SaleCompleted, null, "inventory_movement.delta_must_be_negative")]
    public async Task InvalidMovements_AreValidationErrors_AndStoreNothing(int delta, InventoryMovementReason reason, string? note, string code)
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);

        var result = await RecordAsync(organization, new RecordMovementCommand(product, delta, reason, InventorySourceType.Adjustment, Guid.CreateVersion7(), Guid.CreateVersion7(), note));

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(0, await MovementCountAsync(organization, product));
    }

    [Fact]
    public async Task AnUnknownProduct_AndAnotherOrganizationsProduct_AreNotFound()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await NewProductAsync(theirs);

        var unknown = await RecordAsync(mine, Receive(Guid.CreateVersion7(), 1m));
        var foreign = await RecordAsync(mine, Receive(theirProduct, 1m));

        Assert.Equal("product.not_found", unknown.Error!.Code);
        Assert.Equal("product.not_found", foreign.Error!.Code);
        Assert.Equal(0, await MovementCountAsync(theirs, theirProduct));
        Assert.Equal(0m, await LevelAsync(theirs, theirProduct));
    }

    [Fact]
    public async Task WithoutAnOrganization_NothingIsRecorded()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var (db, recorder) = Start(organization: null);
        await using (db)
        {
            var result = await recorder.RecordAsync(Receive(product, 1m), CancellationToken.None);

            Assert.Equal("identity.forbidden", result.Error!.Code);
        }

        Assert.Equal(0, await MovementCountAsync(organization, product));
    }

    // ---- idempotency

    [Fact]
    public async Task RecordingTheSameMovementAgain_ChangesNothing_AndSaysItWasAlreadyRecorded()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var source = Guid.CreateVersion7();
        var first = await RecordAsync(organization, Receive(product, 5m, source));

        var again = await RecordAsync(organization, Receive(product, 5m, source));

        Assert.True(again.IsSuccess);
        Assert.True(again.Value.AlreadyRecorded);
        Assert.Equal(first.Value.MovementId, again.Value.MovementId);
        Assert.Equal(5m, again.Value.NewQuantity);
        Assert.Equal(5m, await LevelAsync(organization, product));
        Assert.Equal(1, await MovementCountAsync(organization, product));
    }

    [Fact]
    public async Task TheSameSourceWithADifferentReasonOrProduct_IsADifferentMovement()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var other = await NewProductAsync(organization);
        var source = Guid.CreateVersion7();
        await RecordAsync(organization, Receive(product, 5m, source));

        var damage = await RecordAsync(organization, new RecordMovementCommand(product, -1m, InventoryMovementReason.Damage, InventorySourceType.PurchaseReceipt, source, Guid.CreateVersion7(), null));
        var otherProduct = await RecordAsync(organization, Receive(other, 5m, source));

        Assert.False(damage.Value.AlreadyRecorded);
        Assert.False(otherProduct.Value.AlreadyRecorded);
        Assert.Equal(4m, await LevelAsync(organization, product));
    }

    [Fact]
    public async Task WhenAConcurrentRequestRecordsTheSameMovementFirst_TheIndexRefusesOurs_AndTheResultIsAlreadyRecorded()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var source = Guid.CreateVersion7();
        var competitor = new BeforeSaveInterceptor(async () => await RecordAsync(organization, Receive(product, 5m, source)));

        var result = await RecordAsync(organization, Receive(product, 5m, source), competitor);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AlreadyRecorded);
        Assert.Equal(5m, result.Value.NewQuantity);
        Assert.Equal(5m, await LevelAsync(organization, product));
        Assert.Equal(1, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    // ---- optimistic concurrency and retry (ADR 0004)

    [Fact]
    public async Task AfterAConflict_TheRetryReValidates_SoStockThatRanOutIsABusinessErrorNotAConflict()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 1m));
        var interceptions = 0;
        var competitor = new BeforeSaveInterceptor(async () =>
        {
            if (interceptions++ == 0)
            {
                await RecordAsync(organization, Sell(product, 1m));
            }
        });

        var result = await RecordAsync(organization, Sell(product, 1m), competitor);

        Assert.True(result.IsFailure);
        Assert.Equal("stock_level.insufficient_stock", result.Error!.Code);
        Assert.Equal(ErrorKind.RuleViolation, result.Error.Kind);
        Assert.Equal(0m, await LevelAsync(organization, product));
        Assert.Equal(2, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task AfterAConflict_TheRetrySucceeds_WhenThereIsStillEnough()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 10m));
        var interceptions = 0;
        var competitor = new BeforeSaveInterceptor(async () =>
        {
            if (interceptions++ == 0)
            {
                await RecordAsync(organization, Sell(product, 3m));
            }
        });

        var result = await RecordAsync(organization, Sell(product, 2m), competitor);

        Assert.True(result.IsSuccess);
        Assert.Equal(5m, result.Value.NewQuantity);
        Assert.Equal(5m, await LevelAsync(organization, product));
        Assert.Equal(2, interceptions);
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task ARetryThatKeepsConflicting_StopsAfterThreeAttempts_WithAConcurrencyConflict_AndSavesNothing()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 100m));
        var attempts = 0;
        var competitor = new BeforeSaveInterceptor(async () =>
        {
            attempts++;
            await RecordAsync(organization, Sell(product, 1m));
        });

        var result = await RecordAsync(organization, Sell(product, 1m), competitor);

        Assert.Equal("inventory.concurrency_conflict", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal(StockMovementRecorder.MaxAttempts, attempts);
        Assert.Equal(97m, await LevelAsync(organization, product));
        Assert.Equal(1 + StockMovementRecorder.MaxAttempts, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task WhenAConcurrentRequestOpensTheFirstStockLevel_TheLoserRetriesAndBothAreApplied()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var interceptions = 0;
        var competitor = new BeforeSaveInterceptor(async () =>
        {
            if (interceptions++ == 0)
            {
                await RecordAsync(organization, Receive(product, 7m));
            }
        });

        var result = await RecordAsync(organization, Receive(product, 3m), competitor);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, result.Value.NewQuantity);
        Assert.Equal(10m, await LevelAsync(organization, product));
        Assert.Equal(2, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task AFailureThatIsNotARace_IsNotHidden()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        var removeTheProduct = new BeforeSaveInterceptor(async () =>
        {
            await using var admin = database.CreateContext();
            await admin.Database.ExecuteSqlRawAsync("DELETE FROM \"Products\" WHERE \"Id\" = {0}", product);
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => RecordAsync(organization, Receive(product, 1m), removeTheProduct));
    }

    // ---- parallel requests

    [Fact]
    public async Task ParallelSalesOfTheLastUnit_YieldExactlyOneSuccess()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 1m));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RecordAsync(organization, Sell(product, 1m))));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Contains(result.Error!.Code, ExpectedLosses));
        Assert.Equal(0m, await LevelAsync(organization, product));
        Assert.Equal(2, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task ParallelSales_NeverTakeMoreThanThereIs_AndTheStockReconciles()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 5m));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => RecordAsync(organization, Sell(product, 1m))));

        var sold = results.Count(result => result.IsSuccess);
        Assert.InRange(sold, 1, 5);
        Assert.Equal(5m - sold, await LevelAsync(organization, product));
        Assert.True(await LevelAsync(organization, product) >= 0m);
        Assert.Equal(1 + sold, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    [Fact]
    public async Task ParallelReceiptsAndSales_KeepTheStockEqualToTheSumOfTheMovements()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 20m));

        var work = Enumerable.Range(0, 5).Select(_ => RecordAsync(organization, Receive(product, 2m)))
            .Concat(Enumerable.Range(0, 5).Select(_ => RecordAsync(organization, Sell(product, 1m))));
        var results = await Task.WhenAll(work);

        Assert.All(results.Where(result => result.IsFailure), result => Assert.Equal("inventory.concurrency_conflict", result.Error!.Code));
        await AssertReconciledAsync(product);
        Assert.True(await LevelAsync(organization, product) >= 0m);
    }

    [Fact]
    public async Task ParallelRetriesOfTheSameSale_RecordItOnce()
    {
        var organization = await NewOrganizationAsync();
        var product = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(product, 10m));
        var source = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RecordAsync(organization, Sell(product, 2m, source))));

        Assert.Equal(1, results.Count(result => result.IsSuccess && !result.Value.AlreadyRecorded));
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Equal("inventory.concurrency_conflict", result.Error!.Code));
        Assert.Equal(8m, await LevelAsync(organization, product));
        Assert.Equal(2, await MovementCountAsync(organization, product));
        await AssertReconciledAsync(product);
    }

    // ---- several movements in one transaction

    [Fact]
    public async Task SeveralMovementsStagedTogether_AreSavedTogether_OrNotAtAll()
    {
        var organization = await NewOrganizationAsync();
        var first = await NewProductAsync(organization);
        var second = await NewProductAsync(organization);
        await RecordAsync(organization, Receive(first, 5m));
        await RecordAsync(organization, Receive(second, 1m));
        var (db, recorder) = Start(organization);
        await using (db)
        {
            var okLine = await recorder.StageAsync(Sell(first, 2m), CancellationToken.None);
            var shortLine = await recorder.StageAsync(Sell(second, 2m), CancellationToken.None);

            Assert.True(okLine.IsSuccess);
            Assert.Equal("stock_level.insufficient_stock", shortLine.Error!.Code);
            // The caller returns the error without saving, so the first line is not saved either.
        }

        Assert.Equal(5m, await LevelAsync(organization, first));
        Assert.Equal(1m, await LevelAsync(organization, second));

        var (db2, recorder2) = Start(organization);
        await using (db2)
        {
            var result = await recorder2.ExecuteWithRetryAsync(
                async () =>
                {
                    var a = await recorder2.StageAsync(Sell(first, 2m), CancellationToken.None);
                    var b = await recorder2.StageAsync(Sell(second, 1m), CancellationToken.None);
                    Assert.True(a.IsSuccess && b.IsSuccess);
                    await db2.SaveChangesAsync();
                    return Result.Success(new MovementResult(a.Value.MovementId, 0m, false));
                },
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        Assert.Equal(3m, await LevelAsync(organization, first));
        Assert.Equal(0m, await LevelAsync(organization, second));
        await AssertReconciledAsync(first);
        await AssertReconciledAsync(second);
    }

    // ---- reconciliation over a long sequence

    [Fact]
    public async Task AfterAMixedSequenceOfOperations_EveryStockLevelEqualsTheSumOfItsMovements()
    {
        var organization = await NewOrganizationAsync();
        var products = new[] { await NewProductAsync(organization), await NewProductAsync(organization), await NewProductAsync(organization) };
        var random = new Random(42);

        for (var step = 0; step < 60; step++)
        {
            var product = products[random.Next(products.Length)];
            var quantity = random.Next(1, 8);
            var command = random.Next(3) switch
            {
                0 => Receive(product, quantity),
                1 => Sell(product, quantity),
                _ => new RecordMovementCommand(product, random.Next(2) == 0 ? quantity : -quantity, InventoryMovementReason.ManualAdjustment, InventorySourceType.Adjustment, Guid.CreateVersion7(), Guid.CreateVersion7(), "count"),
            };
            await RecordAsync(organization, command);
        }

        Assert.Empty(await InventoryPersistenceTests.ReconciliationMismatchesAsync(database.ConnectionString));
        foreach (var product in products)
        {
            Assert.True(await LevelAsync(organization, product) >= 0m);
        }
    }
}
