using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Catalog;
using Soforr.Application.Catalog.CreateProduct;
using Soforr.Application.Catalog.DeactivateProduct;
using Soforr.Application.Catalog.GetProduct;
using Soforr.Application.Catalog.SearchProducts;
using Soforr.Application.Catalog.UpdateProduct;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Catalog;

/// <summary>
/// The Product handlers on real PostgreSQL (ADR 0005), with a fixed clock and a stand-in caller. They check what the
/// API tests cannot isolate: each outcome per role, per-field validation, the SKU rule, atomicity and paging.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class NewIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    /// <summary>
    /// Runs an action just before the context saves, so a test can make a competing request win the race between the
    /// handler's SKU check and its save, which is otherwise a matter of luck.
    /// </summary>
    private sealed class BeforeSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await action();
            return result;
        }
    }

    private sealed class FixedOrganization(Guid id) : Soforr.Application.Organizations.IOrganizationContext
    {
        public Guid? OrganizationId { get; } = id;
    }

    private AppDbContext CreateContextWithInterceptor(Guid organization, SaveChangesInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptor).Options, new FixedOrganization(organization));

    private async Task<Guid> NewOrganizationAsync(string currency = "GMD")
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Currency.Create(currency), "Europe/London", Now));
        await db.SaveChangesAsync();
        return id;
    }

    private static CreateProductCommand Valid(string sku = "ABC-123", string name = "Rice 25kg") =>
        new(sku, name, "bag", 125.5m, 90.25m, 12m, 7);

    private async Task<Result<ProductDto>> CreateAsync(Guid organization, CreateProductCommand command, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new CreateProductHandler(db, new FixedClock(Now), new NewIds(), FakeUser.In(organization, role)).HandleAsync(command, CancellationToken.None);
    }

    private async Task<Guid> SeedAsync(Guid organization, string sku = "ABC-123", string name = "Rice 25kg")
    {
        var result = await CreateAsync(organization, Valid(sku, name));
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value.Id;
    }

    private static decimal? Parse(string? value) =>
        value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    // ---- create

    [Fact]
    public async Task Create_StoresTheProductInTheCallersOrganization_AndReturnsTheDto()
    {
        var organization = await NewOrganizationAsync("EUR");

        var result = await CreateAsync(organization, new CreateProductCommand("  AbC-1  ", " Rice ", " bag ", 10m, 5m, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal("AbC-1", result.Value.Sku);
        Assert.Equal("Rice", result.Value.Name);
        Assert.Equal("bag", result.Value.UnitOfMeasure);
        Assert.Equal("EUR", result.Value.Currency);
        Assert.Equal(10m, result.Value.SellingPrice);
        Assert.Equal(5m, result.Value.CostPrice);
        Assert.Null(result.Value.ReorderPoint);
        Assert.True(result.Value.IsActive);
        Assert.Equal(Now, result.Value.CreatedAtUtc);

        await using var verify = database.CreateContext(organization);
        var stored = await verify.Set<Product>().SingleAsync(p => p.Id == result.Value.Id);
        Assert.Equal(organization, stored.OrganizationId);
        Assert.Equal("EUR", stored.SellingPrice.Currency.Code);
        Assert.Equal("EUR", stored.CostPrice.Currency.Code);
    }

    [Fact]
    public async Task Create_WithReorderSettings_StoresThem()
    {
        var organization = await NewOrganizationAsync();

        var result = await CreateAsync(organization, Valid());

        Assert.Equal(12m, result.Value.ReorderPoint);
        Assert.Equal(7, result.Value.ReorderLeadTimeDays);
    }

    [Theory]
    [InlineData(null, "Name", "bag", "1", "1", null, null, "product.sku_required", "sku")]
    [InlineData("  ", "Name", "bag", "1", "1", null, null, "product.sku_required", "sku")]
    [InlineData("SKU", "", "bag", "1", "1", null, null, "product.name_required", "name")]
    [InlineData("SKU", "Name", null, "1", "1", null, null, "product.unit_of_measure_required", "unitOfMeasure")]
    [InlineData("SKU", "Name", "bag", null, "1", null, null, "product.selling_price_required", "sellingPrice")]
    [InlineData("SKU", "Name", "bag", "1", null, null, null, "product.cost_price_required", "costPrice")]
    [InlineData("SKU", "Name", "bag", "-1", "1", null, null, "product.selling_price_negative", "sellingPrice")]
    [InlineData("SKU", "Name", "bag", "1", "-1", null, null, "product.cost_price_negative", "costPrice")]
    [InlineData("SKU", "Name", "bag", "1.00001", "1", null, null, "money.scale_exceeded", "sellingPrice")]
    [InlineData("SKU", "Name", "bag", "1", "1.00001", null, null, "money.scale_exceeded", "costPrice")]
    [InlineData("SKU", "Name", "bag", "1", "1", null, 5, "product.reorder_settings_incomplete", "reorderPoint")]
    [InlineData("SKU", "Name", "bag", "1", "1", "5", null, "product.reorder_settings_incomplete", "reorderLeadTimeDays")]
    [InlineData("SKU", "Name", "bag", "1", "1", "-1", 5, "quantity.negative", "reorderPoint")]
    [InlineData("SKU", "Name", "bag", "1", "1", "1", -1, "reorder_settings.lead_time_negative", "reorderLeadTimeDays")]
    public async Task Create_WithInvalidInput_ReturnsAValidationError_NamingTheField_AndStoresNothing(
        string? sku,
        string? name,
        string? unit,
        string? selling,
        string? cost,
        string? reorderPoint,
        int? leadTime,
        string expectedCode,
        string expectedField)
    {
        var organization = await NewOrganizationAsync();
        var command = new CreateProductCommand(sku, name, unit, Parse(selling), Parse(cost), Parse(reorderPoint), leadTime);

        var result = await CreateAsync(organization, command);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(expectedField, result.Error.Field);
        await using var verify = database.CreateContext(organization);
        Assert.Equal(0, await verify.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task Create_WithAPriceBeyondTheStoredRange_IsAValidationError_NotADatabaseError()
    {
        var organization = await NewOrganizationAsync();

        var result = await CreateAsync(organization, new CreateProductCommand("BIG", "Big", "each", 1_000_000_000_000_000m, 1m, null, null));

        Assert.Equal("money.amount_out_of_range", result.Error!.Code);
        Assert.Equal("sellingPrice", result.Error.Field);
    }

    [Theory]
    [InlineData("abc-123")]
    [InlineData("ABC-123")]
    [InlineData("  Abc-123 ")]
    public async Task Create_WithASkuThatExistsInAnyCase_IsAConflict_AndStoresNothing(string sku)
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "ABC-123");

        var result = await CreateAsync(organization, Valid(sku, "Another"));

        Assert.Equal("product.sku_taken", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Equal("sku", result.Error.Field);
        await using var verify = database.CreateContext(organization);
        Assert.Equal(1, await verify.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task Create_WithTheSkuOfAnotherOrganization_IsAllowed()
    {
        var first = await NewOrganizationAsync();
        var second = await NewOrganizationAsync();
        await SeedAsync(first, "SHARED");

        var result = await CreateAsync(second, Valid("shared"));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Create_WhenTwoRequestsRacePastTheCheck_TheIndexDecides_AndTheLoserGetsAConflict()
    {
        var organization = await NewOrganizationAsync();

        var results = await Task.WhenAll(
            CreateAsync(organization, Valid("RACE-1", "One")),
            CreateAsync(organization, Valid("race-1", "Two")),
            CreateAsync(organization, Valid("Race-1", "Three")),
            CreateAsync(organization, Valid("rACE-1", "Four")));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Equal("product.sku_taken", result.Error!.Code));
        await using var verify = database.CreateContext(organization);
        Assert.Equal(1, await verify.Set<Product>().CountAsync());
    }

    [Fact]
    public async Task Create_WhenACompetingRequestWinsAfterTheCheck_TheIndexRefusesTheSave_AndTheResultIsAConflict()
    {
        var organization = await NewOrganizationAsync();
        var interceptor = new BeforeSaveInterceptor(async () => await SeedAsync(organization, "abc-123", "Competitor"));
        await using var db = CreateContextWithInterceptor(organization, interceptor);

        var result = await new CreateProductHandler(db, new FixedClock(Now), new NewIds(), FakeUser.In(organization, Role.Owner))
            .HandleAsync(Valid("ABC-123", "Loser"), CancellationToken.None);

        Assert.Equal("product.sku_taken", result.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        await using var verify = database.CreateContext(organization);
        Assert.Equal(["Competitor"], await verify.Set<Product>().Select(p => p.Name).ToArrayAsync());
    }

    [Fact]
    public async Task Update_WhenACompetingRequestWinsAfterTheCheck_TheIndexRefusesTheSave_AndTheResultIsAConflict()
    {
        var organization = await NewOrganizationAsync();
        var mine = await SeedAsync(organization, "MINE", "Mine");
        var interceptor = new BeforeSaveInterceptor(async () => await SeedAsync(organization, "taken", "Competitor"));
        await using var db = CreateContextWithInterceptor(organization, interceptor);

        var result = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner))
            .HandleAsync(new UpdateProductCommand(mine, "TAKEN", "Renamed", "bag", 1m, 1m, null, null), CancellationToken.None);

        Assert.Equal("product.sku_taken", result.Error!.Code);
        await using var verify = database.CreateContext(organization);
        Assert.Equal("MINE", (await verify.Set<Product>().SingleAsync(p => p.Id == mine)).Sku);
    }

    [Fact]
    public async Task Create_WhenTheSaveFailsForAnotherReason_TheErrorIsNotHiddenAsAConflict()
    {
        var organization = await NewOrganizationAsync();

        // The organization disappears between the handler's checks and its save, so the foreign key fails: a failure
        // that is not a duplicate SKU and must surface, not be reported as one.
        var interceptor = new BeforeSaveInterceptor(async () =>
        {
            await using var admin = database.CreateContext();
            await admin.Database.ExecuteSqlRawAsync("DELETE FROM \"Organizations\" WHERE \"Id\" = {0}", organization);
        });
        await using var db = CreateContextWithInterceptor(organization, interceptor);
        var handler = new CreateProductHandler(db, new FixedClock(Now), new NewIds(), FakeUser.In(organization, Role.Owner));

        await Assert.ThrowsAsync<DbUpdateException>(() => handler.HandleAsync(Valid("ANY"), CancellationToken.None));
    }

    [Theory]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task Create_NeedsTheManagePermission(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();

        var result = await CreateAsync(organization, Valid(), role);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("identity.forbidden", result.Error!.Code);
            await using var verify = database.CreateContext(organization);
            Assert.Equal(0, await verify.Set<Product>().CountAsync());
        }
    }

    [Fact]
    public async Task Create_WithoutBeingSignedIn_IsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new CreateProductHandler(db, new FixedClock(Now), new NewIds(), new FakeUser(null, null, null)).HandleAsync(Valid(), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    // ---- update

    [Fact]
    public async Task Update_ReplacesTheDetails_AndKeepsTheCreationTime()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        var later = Now.AddDays(2);
        await using var db = database.CreateContext(organization);

        var result = await new UpdateProductHandler(db, new FixedClock(later), FakeUser.In(organization, Role.Manager))
            .HandleAsync(new UpdateProductCommand(id, "Abc-123", "Rice 50kg", "sack", 200m, 150m, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Abc-123", result.Value.Sku);
        Assert.Equal("Rice 50kg", result.Value.Name);
        Assert.Equal(200m, result.Value.SellingPrice);
        Assert.Null(result.Value.ReorderPoint);
        Assert.Equal(Now, result.Value.CreatedAtUtc);
        Assert.Equal(later, result.Value.UpdatedAtUtc);
        await using var verify = database.CreateContext(organization);
        Assert.Equal("Abc-123", (await verify.Set<Product>().SingleAsync(p => p.Id == id)).Sku);
    }

    [Fact]
    public async Task Update_ToTheSkuOfAnotherProductInAnotherCase_IsAConflict_AndChangesNothing()
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "ONE", "One");
        var two = await SeedAsync(organization, "TWO", "Two");
        await using var db = database.CreateContext(organization);

        var result = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner))
            .HandleAsync(new UpdateProductCommand(two, "one", "Renamed", "bag", 1m, 1m, null, null), CancellationToken.None);

        Assert.Equal("product.sku_taken", result.Error!.Code);
        await using var verify = database.CreateContext(organization);
        var stored = await verify.Set<Product>().SingleAsync(p => p.Id == two);
        Assert.Equal("TWO", stored.Sku);
        Assert.Equal("Two", stored.Name);
    }

    [Fact]
    public async Task Update_WithAnInvalidValue_ReturnsAValidationError_AndChangesNothing()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        await using var db = database.CreateContext(organization);

        var result = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner))
            .HandleAsync(new UpdateProductCommand(id, "NEW-SKU", "", "bag", 1m, 1m, null, null), CancellationToken.None);

        Assert.Equal("product.name_required", result.Error!.Code);
        Assert.Equal("name", result.Error.Field);
        await using var verify = database.CreateContext(organization);
        Assert.Equal("ABC-123", (await verify.Set<Product>().SingleAsync(p => p.Id == id)).Sku);
    }

    [Fact]
    public async Task Update_OfAnotherOrganizationsProduct_IsNotFound_AndChangesNothing()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await SeedAsync(theirs, "THEIRS", "Theirs");
        await using var db = database.CreateContext(mine);

        var result = await new UpdateProductHandler(db, new FixedClock(Now), FakeUser.In(mine, Role.Owner))
            .HandleAsync(new UpdateProductCommand(theirProduct, "HIJACKED", "Hijacked", "bag", 1m, 1m, null, null), CancellationToken.None);

        Assert.Equal("product.not_found", result.Error!.Code);
        await using var verify = database.CreateContext(theirs);
        Assert.Equal("THEIRS", (await verify.Set<Product>().SingleAsync(p => p.Id == theirProduct)).Sku);
    }

    [Fact]
    public async Task Update_WithoutThePermission_IsForbidden_AndChangesNothing()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        await using var db = database.CreateContext(organization);

        var result = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Staff))
            .HandleAsync(new UpdateProductCommand(id, "X", "X", "x", 1m, 1m, null, null), CancellationToken.None);

        Assert.Equal("identity.forbidden", result.Error!.Code);
        await using var verify = database.CreateContext(organization);
        Assert.Equal("Rice 25kg", (await verify.Set<Product>().SingleAsync(p => p.Id == id)).Name);
    }

    [Fact]
    public async Task Update_ToATakenSku_ReturnsTheConflictBeforeTouchingTheProduct_AndNeverSaves()
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "ONE", "One");
        var two = await SeedAsync(organization, "TWO", "Two");
        var saves = 0;
        await using var db = CreateContextWithInterceptor(organization, new BeforeSaveInterceptor(() =>
        {
            saves++;
            return Task.CompletedTask;
        }));

        var result = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner))
            .HandleAsync(new UpdateProductCommand(two, "one", "Renamed", "sack", 99m, 98m, 1m, 1), CancellationToken.None);

        Assert.Equal("product.sku_taken", result.Error!.Code);
        Assert.Equal(0, saves);

        // The unit of work is left clean: the tracked product is unchanged, not half-updated.
        var tracked = Assert.Single(db.ChangeTracker.Entries<Product>());
        Assert.Equal(EntityState.Unchanged, tracked.State);
        Assert.Equal("TWO", tracked.Entity.Sku);
        Assert.Equal("Two", tracked.Entity.Name);
        Assert.Equal("bag", tracked.Entity.UnitOfMeasure);
        Assert.Equal(125.5m, tracked.Entity.SellingPrice.Amount);
        Assert.Equal(Now, tracked.Entity.UpdatedAtUtc);

        await using var verify = database.CreateContext(organization);
        var stored = await verify.Set<Product>().SingleAsync(p => p.Id == two);
        Assert.Equal("TWO", stored.Sku);
        Assert.Equal("Two", stored.Name);
    }

    [Fact]
    public async Task Update_WhenTheSaveFailsForAnotherReason_TheErrorIsNotHiddenAsAConflict()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);

        // The product row disappears between the handler's checks and its save: a failure that is not a duplicate SKU.
        var interceptor = new BeforeSaveInterceptor(async () =>
        {
            await using var admin = database.CreateContext();
            await admin.Database.ExecuteSqlRawAsync("DELETE FROM \"Products\" WHERE \"Id\" = {0}", id);
        });
        await using var db = CreateContextWithInterceptor(organization, interceptor);
        var handler = new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => handler.HandleAsync(new UpdateProductCommand(id, "OTHER-SKU", "Renamed", "bag", 1m, 1m, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Update_OfADeactivatedProduct_IsAllowed_AndItStaysInactive()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        await using (var db = database.CreateContext(organization))
        {
            await new DeactivateProductHandler(db, new FixedClock(Now), FakeUser.In(organization, Role.Owner)).HandleAsync(new DeactivateProductCommand(id), CancellationToken.None);
        }

        await using var update = database.CreateContext(organization);
        var result = await new UpdateProductHandler(update, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Manager))
            .HandleAsync(new UpdateProductCommand(id, "ABC-123", "Renamed while inactive", "bag", 2m, 1m, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed while inactive", result.Value.Name);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task ADeactivatedProductKeepsItsSku_UntilAnAuthorizedUpdateChangesIt()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization, "OLD-SKU", "Old");
        await using (var db = database.CreateContext(organization))
        {
            await new DeactivateProductHandler(db, new FixedClock(Now), FakeUser.In(organization, Role.Owner)).HandleAsync(new DeactivateProductCommand(id), CancellationToken.None);
        }

        var whileReserved = await CreateAsync(organization, Valid("old-sku", "Reuse"));
        await using (var db = database.CreateContext(organization))
        {
            var renamed = await new UpdateProductHandler(db, new FixedClock(Now.AddDays(1)), FakeUser.In(organization, Role.Owner))
                .HandleAsync(new UpdateProductCommand(id, "NEW-SKU", "Old", "bag", 1m, 1m, null, null), CancellationToken.None);
            Assert.True(renamed.IsSuccess);
        }

        var afterRename = await CreateAsync(organization, Valid("old-sku", "Reuse"));

        Assert.Equal("product.sku_taken", whileReserved.Error!.Code);
        Assert.True(afterRename.IsSuccess);
    }

    // ---- deactivate

    [Fact]
    public async Task Deactivate_TakesTheProductOutOfUse_AndRepeatingItChangesNothing()
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        var firstTime = Now.AddDays(1);

        await using (var db = database.CreateContext(organization))
        {
            var result = await new DeactivateProductHandler(db, new FixedClock(firstTime), FakeUser.In(organization, Role.Owner)).HandleAsync(new DeactivateProductCommand(id), CancellationToken.None);
            Assert.False(result.Value.IsActive);
        }

        await using (var db = database.CreateContext(organization))
        {
            var again = await new DeactivateProductHandler(db, new FixedClock(Now.AddDays(5)), FakeUser.In(organization, Role.Owner)).HandleAsync(new DeactivateProductCommand(id), CancellationToken.None);
            Assert.False(again.Value.IsActive);
            Assert.Equal(firstTime, again.Value.UpdatedAtUtc);
        }
    }

    [Fact]
    public async Task Deactivate_NeedsThePermission_AndOfAForeignProductIsNotFound()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await SeedAsync(theirs);
        var mineProduct = await SeedAsync(mine, "MINE", "Mine");

        await using var asStaff = database.CreateContext(mine);
        var forbidden = await new DeactivateProductHandler(asStaff, new FixedClock(Now), FakeUser.In(mine, Role.Staff)).HandleAsync(new DeactivateProductCommand(mineProduct), CancellationToken.None);
        await using var asOwner = database.CreateContext(mine);
        var notFound = await new DeactivateProductHandler(asOwner, new FixedClock(Now), FakeUser.In(mine, Role.Owner)).HandleAsync(new DeactivateProductCommand(theirProduct), CancellationToken.None);

        Assert.Equal("identity.forbidden", forbidden.Error!.Code);
        Assert.Equal("product.not_found", notFound.Error!.Code);
        await using var verify = database.CreateContext(theirs);
        Assert.True((await verify.Set<Product>().SingleAsync(p => p.Id == theirProduct)).IsActive);
    }

    // ---- get

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, true)]
    [InlineData(Role.Staff, false)]
    public async Task Get_ShowsTheCostPriceOnlyToThoseWhoMaySeeIt(Role role, bool seesCost)
    {
        var organization = await NewOrganizationAsync();
        var id = await SeedAsync(organization);
        await using var db = database.CreateContext(organization);

        var result = await new GetProductHandler(db, FakeUser.In(organization, role)).HandleAsync(new GetProductQuery(id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(125.5m, result.Value.SellingPrice);
        Assert.Equal(seesCost ? 90.25m : null, result.Value.CostPrice);
    }

    [Fact]
    public async Task Get_OfAForeignOrAMissingProduct_IsNotFound_WithTheSameAnswer()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        var theirProduct = await SeedAsync(theirs);
        await using var db = database.CreateContext(mine);
        var handler = new GetProductHandler(db, FakeUser.In(mine, Role.Owner));

        var foreign = await handler.HandleAsync(new GetProductQuery(theirProduct), CancellationToken.None);
        var missing = await handler.HandleAsync(new GetProductQuery(Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal("product.not_found", foreign.Error!.Code);
        Assert.Equal("product.not_found", missing.Error!.Code);
    }

    [Fact]
    public async Task Get_WithoutBeingSignedIn_IsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new GetProductHandler(db, new FakeUser(null, null, null)).HandleAsync(new GetProductQuery(Guid.CreateVersion7()), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    // ---- search

    private async Task<Result<PagedResult<ProductDto>>> SearchAsync(Guid organization, SearchProductsQuery query, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new SearchProductsHandler(db, FakeUser.In(organization, role)).HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Search_MatchesPartOfTheSkuOrTheName_WithoutRegardToCase()
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "RICE-25", "Basmati");
        await SeedAsync(organization, "OIL-1", "Rice bran oil");
        await SeedAsync(organization, "SALT-1", "Sea salt");

        var byText = await SearchAsync(organization, new SearchProductsQuery("rIcE", false, 1, 25));
        var bySku = await SearchAsync(organization, new SearchProductsQuery("salt-", false, 1, 25));
        var none = await SearchAsync(organization, new SearchProductsQuery("zzz", false, 1, 25));

        Assert.Equal(["Basmati", "Rice bran oil"], byText.Value.Items.Select(p => p.Name).ToArray());
        Assert.Equal(2, byText.Value.TotalCount);
        Assert.Equal(["Sea salt"], bySku.Value.Items.Select(p => p.Name).ToArray());
        Assert.Empty(none.Value.Items);
        Assert.Equal(0, none.Value.TotalCount);
    }

    [Fact]
    public async Task Search_TreatsWildcardCharactersInTheTextLiterally()
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "A-1", "Plain");
        await SeedAsync(organization, "B-1", "100% cotton");

        var percent = await SearchAsync(organization, new SearchProductsQuery("%", false, 1, 25));
        var underscore = await SearchAsync(organization, new SearchProductsQuery("_", false, 1, 25));

        Assert.Equal(["100% cotton"], percent.Value.Items.Select(p => p.Name).ToArray());
        Assert.Empty(underscore.Value.Items);
    }

    [Fact]
    public async Task Search_LeavesOutInactiveProducts_UnlessAskedToInclude()
    {
        var organization = await NewOrganizationAsync();
        await SeedAsync(organization, "LIVE", "Live");
        var old = await SeedAsync(organization, "OLD", "Old");
        await using (var db = database.CreateContext(organization))
        {
            await new DeactivateProductHandler(db, new FixedClock(Now), FakeUser.In(organization, Role.Owner)).HandleAsync(new DeactivateProductCommand(old), CancellationToken.None);
        }

        var active = await SearchAsync(organization, new SearchProductsQuery(null, false, 1, 25));
        var all = await SearchAsync(organization, new SearchProductsQuery(null, true, 1, 25));

        Assert.Equal(["Live"], active.Value.Items.Select(p => p.Name).ToArray());
        Assert.Equal(["Live", "Old"], all.Value.Items.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Search_PagesInAStableOrder_AndReportsTheTotal()
    {
        var organization = await NewOrganizationAsync();
        for (var index = 1; index <= 5; index++)
        {
            await SeedAsync(organization, $"P-{index}", $"Product {index}");
        }

        var first = await SearchAsync(organization, new SearchProductsQuery(null, false, 1, 2));
        var second = await SearchAsync(organization, new SearchProductsQuery(null, false, 2, 2));
        var third = await SearchAsync(organization, new SearchProductsQuery(null, false, 3, 2));
        var beyond = await SearchAsync(organization, new SearchProductsQuery(null, false, 4, 2));

        Assert.Equal(["Product 1", "Product 2"], first.Value.Items.Select(p => p.Name).ToArray());
        Assert.Equal(["Product 3", "Product 4"], second.Value.Items.Select(p => p.Name).ToArray());
        Assert.Equal(["Product 5"], third.Value.Items.Select(p => p.Name).ToArray());
        Assert.Empty(beyond.Value.Items);
        Assert.All(new[] { first, second, third, beyond }, page => Assert.Equal(5, page.Value.TotalCount));
        Assert.Equal(2, second.Value.Page);
        Assert.Equal(2, second.Value.PageSize);
    }

    [Theory]
    [InlineData(0, 25, "pagination.page_invalid", "page")]
    [InlineData(-1, 25, "pagination.page_invalid", "page")]
    [InlineData(1_000_001, 25, "pagination.page_invalid", "page")]
    [InlineData(1, 0, "pagination.page_size_invalid", "pageSize")]
    [InlineData(1, 101, "pagination.page_size_invalid", "pageSize")]
    [InlineData(1, int.MaxValue, "pagination.page_size_invalid", "pageSize")]
    public async Task Search_RejectsPagingOutsideTheBounds_InsteadOfCorrectingIt(int page, int pageSize, string code, string field)
    {
        var organization = await NewOrganizationAsync();

        var result = await SearchAsync(organization, new SearchProductsQuery(null, false, page, pageSize));

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(field, result.Error.Field);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Search_RejectsTextThatIsTooLong()
    {
        var organization = await NewOrganizationAsync();

        var result = await SearchAsync(organization, new SearchProductsQuery(new string('x', SearchProductsQuery.MaxTextLength + 1), false, 1, 25));

        Assert.Equal("product.search_text_too_long", result.Error!.Code);
        Assert.Equal("q", result.Error.Field);
    }

    [Fact]
    public async Task Search_SeesOnlyTheCallersOrganization_AndHidesCostFromStaff()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        await SeedAsync(mine, "MINE", "Mine");
        await SeedAsync(theirs, "THEIRS", "Theirs");

        var asStaff = await SearchAsync(mine, new SearchProductsQuery(null, true, 1, 25), Role.Staff);
        var asOwner = await SearchAsync(mine, new SearchProductsQuery(null, true, 1, 25), Role.Owner);

        Assert.Equal(["Mine"], asStaff.Value.Items.Select(p => p.Name).ToArray());
        Assert.All(asStaff.Value.Items, product => Assert.Null(product.CostPrice));
        Assert.All(asOwner.Value.Items, product => Assert.Equal(90.25m, product.CostPrice));
    }

    [Fact]
    public async Task Search_WithoutBeingSignedIn_IsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new SearchProductsHandler(db, new FakeUser(null, null, null)).HandleAsync(new SearchProductsQuery(null, false, 1, 25), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }
}
