using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Offline;
using Soforr.Application.Offline.IssueOfflineGrant;
using Soforr.Application.Offline.RecordOfflineContact;
using Soforr.Application.Organizations;
using Soforr.Application.Sales;
using Soforr.Application.Sales.CreateSale;
using Soforr.Application.Sales.RecordPayment;
using Soforr.Domain.Audit;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Identity;
using Soforr.Domain.Inventory;
using Soforr.Domain.Offline;
using Soforr.Domain.Organizations;
using Soforr.Domain.Sales;
using Soforr.Infrastructure.Persistence;

namespace Soforr.Api.IntegrationTests.Offline;

/// <summary>
/// The offline authorization boundary (ADR 0008) and the replay contract on real PostgreSQL, with a clock the test moves: grants and
/// contacts, the legitimate silent gap in both orders, the backdating attack, every boundary, permissions and tenants, price drift,
/// idempotent replay (including the lost response), races, pruning and audit. The unit tests of the pure rule are in the Domain tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OfflineHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Monday10 = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Currency Gmd = Currency.Create("GMD");

    private sealed class TestClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
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
    }

    /// <summary>Audit that joins the request's own unit of work, as the real one does, with a fixed time.</summary>
    private sealed class UnitOfWorkAuditLog(AppDbContext db, IClock clock) : IAuditLog
    {
        public void Record(AuditEntry entry) =>
            db.Set<AuditEvent>().Add(AuditEvent.Record(Guid.CreateVersion7(), entry.OrganizationId, entry.ActorId, entry.Action, entry.EntityType, entry.EntityId, entry.Description, "test-corr", clock.UtcNow));
    }

    /// <summary>An organization with a priced product (10, cost 6), stock and a customer on 30 day terms, and an owner.</summary>
    private sealed record Env(Guid Organization, FakeUser Owner, Guid Product, Guid Customer, TestClock Clock)
    {
        public FakeUser As(Role? role, Guid? organization = null) => new(Guid.CreateVersion7(), organization ?? Organization, role);
    }

    private async Task<Env> NewEnvAsync(decimal stock = 100m, int termsDays = 30)
    {
        var organization = Guid.CreateVersion7();
        await using (var db = database.CreateContext())
        {
            db.Set<Organization>().Add(Organization.Create(organization, "Soforr Trading", Gmd, "UTC", Monday10.AddYears(-1)));
            await db.SaveChangesAsync();
        }

        var owner = new FakeUser(Guid.CreateVersion7(), organization, Role.Owner);
        var clock = new TestClock(Monday10);
        var product = Product.Create(Guid.CreateVersion7(), organization, "SKU-" + Guid.NewGuid().ToString("N"), "Rice", "bag", Money.Create(10m, Gmd), Money.Create(6m, Gmd), null, Monday10.AddYears(-1));
        var customer = Customer.Create(Guid.CreateVersion7(), organization, "Awa Jallow", null, termsDays, Monday10.AddYears(-1));
        await using (var db = database.CreateContext(organization))
        {
            db.Set<Product>().Add(product);
            db.Set<Customer>().Add(customer);
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext(organization))
        {
            var recorded = await new StockMovementRecorder(db, owner, clock, new NewIds()).RecordAsync(
                new RecordMovementCommand(product.Id, stock, InventoryMovementReason.PurchaseReceived, InventorySourceType.PurchaseReceipt, Guid.CreateVersion7(), owner.UserId!.Value, null),
                CancellationToken.None);
            Assert.True(recorded.IsSuccess, recorded.Error?.Code);
        }

        return new Env(organization, owner, product.Id, customer.Id, clock);
    }

    private AppDbContext Context(FakeUser user) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options, user);

    private static OfflineEventTimeValidator Validator(AppDbContext db, FakeUser user, TestClock clock) =>
        new(db, clock, user, TestOfflineSettings.Default, new UnitOfWorkAuditLog(db, clock));

    private async Task<Result<OfflineGrantDto>> IssueAsync(Env env, FakeUser? user = null)
    {
        user ??= env.Owner;
        await using var db = Context(user);
        return await new IssueOfflineGrantHandler(db, env.Clock, new NewIds(), user, TestOfflineSettings.Default, new UnitOfWorkAuditLog(db, env.Clock))
            .HandleAsync(new IssueOfflineGrantCommand(), CancellationToken.None);
    }

    private async Task<Result<OfflineGrantDto>> ContactAsync(Env env, Guid grant, FakeUser? user = null)
    {
        user ??= env.Owner;
        await using var db = Context(user);
        return await new RecordOfflineContactHandler(db, env.Clock, new NewIds(), user, TestOfflineSettings.Default)
            .HandleAsync(new RecordOfflineContactCommand(grant), CancellationToken.None);
    }

    private async Task<Result<SaleResultDto>> SellAsync(
        Env env,
        Guid? grant,
        DateTimeOffset? occurredAt,
        Guid? saleId = null,
        decimal quantity = 2m,
        decimal? shown = 10m,
        decimal? unitPrice = null,
        string? reason = null,
        bool onAccount = false,
        decimal? amountPaid = null,
        FakeUser? user = null)
    {
        user ??= env.Owner;
        await using var db = Context(user);
        var handler = new CreateSaleHandler(
            db,
            new StockMovementRecorder(db, user, env.Clock, new NewIds()),
            env.Clock,
            new NewIds(),
            user,
            new UnitOfWorkAuditLog(db, env.Clock),
            Validator(db, user, env.Clock));

        return await handler.HandleAsync(
            new CreateSaleCommand(saleId, [new CreateSaleLine(env.Product, quantity, unitPrice, reason, shown)], onAccount ? env.Customer : null, amountPaid, null, occurredAt, grant),
            CancellationToken.None);
    }

    private async Task<Result<PaymentResultDto>> PayAsync(Env env, Guid sale, decimal amount, Guid? grant, DateTimeOffset? paidAt, Guid? paymentId = null, FakeUser? user = null)
    {
        user ??= env.Owner;
        await using var db = Context(user);
        return await new RecordPaymentHandler(db, env.Clock, new NewIds(), user, new UnitOfWorkAuditLog(db, env.Clock), Validator(db, user, env.Clock))
            .HandleAsync(new RecordPaymentCommand(sale, paymentId, amount, paidAt, "Cash", grant), CancellationToken.None);
    }

    private async Task<(int Sales, int Movements, int SaleAudits, int Accepted, int Refused, decimal Stock)> StateAsync(Env env)
    {
        await using var db = database.CreateContext(env.Organization);
        return (
            await db.Set<Sale>().CountAsync(),
            await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.SaleCreated),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.OfflineSaleAccepted || e.Action == AuditActions.OfflinePaymentAccepted),
            await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.OfflineEventRefused),
            (await db.Set<StockLevel>().SingleAsync(level => level.ProductId == env.Product)).Quantity.Value);
    }

    /// <summary>Issues a grant at 10:00 and reports contact at 10:05, 10:10 and 10:15; the clock ends at 10:15.</summary>
    private async Task<Guid> GrantWithContactUntil1015Async(Env env)
    {
        env.Clock.UtcNow = Monday10;
        var grant = (await IssueAsync(env)).Value.GrantId;
        foreach (var minute in new[] { 5, 10, 15 })
        {
            env.Clock.UtcNow = Monday10.AddMinutes(minute);
            Assert.True((await ContactAsync(env, grant)).IsSuccess);
        }

        return grant;
    }

    private static DateTimeOffset At(int hour, int minute = 0, int day = 0) => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day).AddHours(hour).AddMinutes(minute);

    // ---- grants and contact

    [Fact]
    public async Task IssuingAGrant_StoresItWithTheServersTimes_ItsFirstContact_AndAnAuditEvent()
    {
        var env = await NewEnvAsync();

        var result = await IssueAsync(env);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var grant = result.Value;
        Assert.Equal((Monday10, Monday10, Monday10.AddHours(72), Monday10.AddDays(30), 72 * 3600, 300), (grant.IssuedAtUtc, grant.LastContactAtUtc, grant.ExpiresAtUtc, grant.AbsoluteExpiresAtUtc, grant.WindowSeconds, grant.ContactIntervalSeconds));
        Assert.Equal(Monday10, grant.ServerTimeUtc);
        await using var db = database.CreateContext(env.Organization);
        var stored = await db.Set<OfflineGrant>().SingleAsync(g => g.Id == grant.GrantId);
        Assert.Equal((env.Organization, env.Owner.UserId!.Value), (stored.OrganizationId, stored.UserId));
        Assert.Equal(1, await db.Set<OfflineContact>().CountAsync(c => c.GrantId == grant.GrantId));
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.OfflineGrantIssued && e.EntityId == grant.GrantId));
    }

    [Fact]
    public async Task ANewGrant_SupersedesTheUsersPreviousOne_WhichStopsTakingContact()
    {
        var env = await NewEnvAsync();
        var first = (await IssueAsync(env)).Value.GrantId;
        env.Clock.UtcNow = Monday10.AddHours(1);

        var second = (await IssueAsync(env)).Value.GrantId;
        var contactOnFirst = await ContactAsync(env, first);
        var contactOnSecond = await ContactAsync(env, second);

        Assert.Equal("offline.grant_superseded", contactOnFirst.Error!.Code);
        Assert.True(contactOnSecond.IsSuccess);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(Monday10.AddHours(1), (await db.Set<OfflineGrant>().SingleAsync(g => g.Id == first)).SupersededAtUtc);
        Assert.Equal(1, await db.Set<OfflineGrant>().CountAsync(g => g.SupersededAtUtc == null));
    }

    [Fact]
    public async Task SimultaneousIssuances_LeaveExactlyOneActiveGrant()
    {
        var env = await NewEnvAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => IssueAsync(env)));

        Assert.Contains(results, r => r.IsSuccess);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(1, await db.Set<OfflineGrant>().CountAsync(g => g.UserId == env.Owner.UserId!.Value && g.SupersededAtUtc == null && g.RevokedAtUtc == null));
        Assert.Equal(results.Count(r => r.IsSuccess), await db.Set<OfflineGrant>().CountAsync());
    }

    [Fact]
    public async Task Contact_IsRecordedWithTheServersClock_AndTwoSimultaneousContactsAreBothKept()
    {
        var env = await NewEnvAsync();
        var grant = (await IssueAsync(env)).Value.GrantId;
        env.Clock.UtcNow = Monday10.AddMinutes(5);

        var results = await Task.WhenAll(ContactAsync(env, grant), ContactAsync(env, grant));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Code));
        await using var db = database.CreateContext(env.Organization);
        var contacts = await db.Set<OfflineContact>().Where(c => c.GrantId == grant).Select(c => c.ObservedAtUtc).ToListAsync();
        Assert.Equal([Monday10, Monday10.AddMinutes(5), Monday10.AddMinutes(5)], contacts.Order().ToArray());
    }

    [Fact]
    public async Task Contact_IsRefused_ForAnotherUsersGrant_AnotherOrganizationsGrant_ARevokedGrant_AndAnExpiredOne()
    {
        var env = await NewEnvAsync();
        var other = await NewEnvAsync();
        var grant = (await IssueAsync(env)).Value.GrantId;

        var anotherUser = await ContactAsync(env, grant, env.As(Role.Owner));
        var anotherOrganization = await ContactAsync(other, grant);
        env.Clock.UtcNow = Monday10.AddHours(72).AddTicks(10);
        var tooSilent = await ContactAsync(env, grant);
        env.Clock.UtcNow = Monday10.AddHours(1);
        await using (var db = Context(env.Owner))
        {
            (await db.Set<OfflineGrant>().SingleAsync(g => g.Id == grant)).Revoke(env.Clock.UtcNow);
            await db.SaveChangesAsync();
        }

        var revoked = await ContactAsync(env, grant);

        Assert.Equal("offline.grant_not_found", anotherUser.Error!.Code);
        Assert.Equal("offline.grant_not_found", anotherOrganization.Error!.Code);
        Assert.Equal("offline.grant_expired", tooSilent.Error!.Code);
        Assert.Equal("offline.grant_revoked", revoked.Error!.Code);
    }

    [Fact]
    public async Task Contact_IsAcceptedAfterExactlyTheWindowOfSilence_ButNotAfterTheAbsoluteExpiry()
    {
        var env = await NewEnvAsync();
        var grant = (await IssueAsync(env)).Value.GrantId;

        env.Clock.UtcNow = Monday10.AddHours(72);
        var atWindow = await ContactAsync(env, grant);
        env.Clock.UtcNow = Monday10.AddDays(30).AddTicks(10);
        var pastExpiry = await ContactAsync(env, grant);

        Assert.True(atWindow.IsSuccess, atWindow.Error?.Code);
        Assert.Equal("offline.grant_expired", pastExpiry.Error!.Code);
    }

    [Fact]
    public async Task AUserWithoutAMembership_CannotHoldAGrantOrReportContact()
    {
        var env = await NewEnvAsync();
        var noRole = env.As(null);
        var noOrganization = new FakeUser(Guid.CreateVersion7(), null, Role.Owner);

        Assert.False((await IssueAsync(env, noRole)).IsSuccess);
        Assert.False((await IssueAsync(env, noOrganization)).IsSuccess);
        Assert.False((await ContactAsync(env, Guid.CreateVersion7(), noRole)).IsSuccess);
    }

    // ---- the legitimate gap, in both orders

    [Fact]
    public async Task ASaleInASilentGap_IsAccepted_WhenItIsSynchronizedBeforeTheNextContact_AndKeepsItsEventTime()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();

        var result = await SellAsync(env, grant, At(11), saleId, onAccount: true, amountPaid: 5m);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var sale = result.Value.Sale;
        Assert.False(result.Value.AlreadyRecorded);
        Assert.Equal((At(11), At(11, 3), grant, At(11).AddDays(30)), (sale.CreatedAtUtc, sale.ReceivedAtUtc, sale.OfflineGrantId, sale.DueAtUtc));
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));

        // The contact that closes the gap afterwards changes nothing about the sale or the evidence.
        env.Clock.UtcNow = At(11, 5);
        Assert.True((await ContactAsync(env, grant)).IsSuccess);
        Assert.True((await SellAsync(env, grant, At(11), saleId, onAccount: true, amountPaid: 5m)).Value.AlreadyRecorded);
    }

    [Fact]
    public async Task TheSameSale_IsAccepted_WhenTheClosingContactArrivesFirst()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 5);
        Assert.True((await ContactAsync(env, grant)).IsSuccess);
        env.Clock.UtcNow = At(11, 7);

        var result = await SellAsync(env, grant, At(11));

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task ASaleRacingWithTheClosingContact_IsAcceptedWhicheverWins()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 5);

        var contact = ContactAsync(env, grant);
        var sale = SellAsync(env, grant, At(11));
        await Task.WhenAll(contact, sale);

        Assert.True((await contact).IsSuccess);
        var sold = await sale;
        Assert.True(sold.IsSuccess, sold.Error?.Code);
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    // ---- the attack and the boundaries

    [Fact]
    public async Task BackdatingIntoAPeriodWhenTheServerSawContact_IsRefused_RecordsNothing_AndReservesNoId()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(10);
        var grant = (await IssueAsync(env)).Value.GrantId;

        // Monday 10:00 to 12:00 the device was in contact every five minutes, then silent.
        for (var minute = 5; minute <= 120; minute += 5)
        {
            env.Clock.UtcNow = At(10).AddMinutes(minute);
            Assert.True((await ContactAsync(env, grant)).IsSuccess);
        }

        // Tuesday 15:00 a request claims Monday 11:00: 28 hours old, inside the 72 hour window, but the server saw the device then.
        env.Clock.UtcNow = At(15, day: 1);
        var saleId = Guid.CreateVersion7();

        var attack = await SellAsync(env, grant, At(11), saleId);

        Assert.Equal("offline.event_time_unverifiable", attack.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, attack.Error.Kind);
        Assert.Equal((0, 0, 0, 0, 1, 100m), await StateAsync(env));
        await using (var db = database.CreateContext(env.Organization))
        {
            Assert.Equal(0, await db.Set<Sale>().CountAsync());
            Assert.Equal(0, await db.Set<SaleLine>().CountAsync());
            Assert.Equal(0, await db.Set<SalePayment>().CountAsync());
            Assert.Equal(0, await db.Set<InventoryMovement>().CountAsync(m => m.Reason == InventoryMovementReason.SaleCompleted));
            Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.SaleCreated || e.Action == AuditActions.OfflineSaleAccepted));
        }

        // The refused request did not reserve the id: the same id with a time the server can vouch for is accepted.
        var corrected = await SellAsync(env, grant, At(13), saleId);
        Assert.True(corrected.IsSuccess, corrected.Error?.Code);
        Assert.Equal((1, 1, 1, 1, 1, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task WithoutAGrant_NoEventTimeIsTrusted_AndNoTimeIsSilentlyReplacedByServerTime()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(12);

        var noGrant = await SellAsync(env, null, At(11));
        var noTime = await SellAsync(env, Guid.CreateVersion7(), null);
        var emptyGrant = await SellAsync(env, Guid.Empty, At(11));
        var unknown = await SellAsync(env, Guid.CreateVersion7(), At(11));

        Assert.Equal("offline.grant_required", noGrant.Error!.Code);
        Assert.Equal("offline.event_time_required", noTime.Error!.Code);
        Assert.Equal("offline.grant_invalid", emptyGrant.Error!.Code);
        Assert.Equal("offline.grant_not_found", unknown.Error!.Code);
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(env));
    }

    [Fact]
    public async Task ATimeWithAnOffset_IsRefused()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);

        var result = await SellAsync(env, grant, new DateTimeOffset(At(11).DateTime, TimeSpan.FromHours(1)));

        Assert.Equal("sale.timestamp_not_utc", result.Error!.Code);
    }

    [Fact]
    public async Task ExactlySeventyTwoHoursAfterTheGapBegan_IsAccepted_AndTheNextMicrosecondIsNot()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(10, 15, day: 3).AddHours(1);

        var exactly = await SellAsync(env, grant, At(10, 15, day: 3));
        var beyond = await SellAsync(env, grant, At(10, 15, day: 3).AddTicks(10));

        Assert.True(exactly.IsSuccess, exactly.Error?.Code);
        Assert.Equal("offline.event_time_outside_window", beyond.Error!.Code);
        Assert.Equal((1, 1, 1, 1, 1, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task AFutureTime_IsRefusedBeyondTheSkew_AndTheSkewNeverMakesAFutureTimeVouchedFor()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);

        var tooFar = await SellAsync(env, grant, At(12).AddMinutes(2).AddTicks(10));
        var withinSkew = await SellAsync(env, grant, At(12).AddSeconds(30));

        Assert.Equal("offline.event_time_in_future", tooFar.Error!.Code);
        Assert.Equal(ErrorKind.Validation, tooFar.Error.Kind);
        Assert.Equal("offline.event_time_unverifiable", withinSkew.Error!.Code);
        Assert.Equal((0, 0, 0, 0, 2, 100m), await StateAsync(env));
    }

    [Fact]
    public async Task AGrantThatWasRevoked_AnExpiredOne_AndASupersededOne_VouchForNothingAfterTheirEnd()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);
        await using (var db = Context(env.Owner))
        {
            (await db.Set<OfflineGrant>().SingleAsync(g => g.Id == grant)).Supersede(At(11, 30));
            await db.SaveChangesAsync();
        }

        var beforeReplacement = await SellAsync(env, grant, At(11));
        var afterReplacement = await SellAsync(env, grant, At(11, 45));
        await using (var db = Context(env.Owner))
        {
            (await db.Set<OfflineGrant>().SingleAsync(g => g.Id == grant)).Revoke(At(12));
            await db.SaveChangesAsync();
        }

        var revoked = await SellAsync(env, grant, At(11, 1));

        Assert.True(beforeReplacement.IsSuccess, beforeReplacement.Error?.Code);
        Assert.Equal("offline.grant_superseded", afterReplacement.Error!.Code);
        Assert.Equal("offline.grant_revoked", revoked.Error!.Code);
    }

    [Fact]
    public async Task ATimeAfterTheGrantsAbsoluteExpiry_IsRefused()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(10, 15, day: 31);

        var result = await SellAsync(env, grant, At(10, 16, day: 30));

        Assert.Equal("offline.grant_expired", result.Error!.Code);
    }

    // ---- identity, tenants and permissions

    [Fact]
    public async Task AnotherUsersGrant_AndAnotherOrganizationsGrant_CannotBeUsed()
    {
        var env = await NewEnvAsync();
        var other = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        var otherGrant = await GrantWithContactUntil1015Async(other);
        env.Clock.UtcNow = At(12);
        other.Clock.UtcNow = At(12);

        var otherUser = await SellAsync(env, grant, At(11), user: env.As(Role.Owner));
        var foreignOrganization = await SellAsync(env, otherGrant, At(11));

        Assert.Equal("offline.grant_not_found", otherUser.Error!.Code);
        Assert.Equal("offline.grant_not_found", foreignOrganization.Error!.Code);
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(env));
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(other));
    }

    [Fact]
    public async Task AUserWhoseMembershipIsGone_CannotReplay()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);
        var removed = new FakeUser(env.Owner.UserId, null, Role.Owner);
        var noRole = new FakeUser(env.Owner.UserId, env.Organization, null);

        var withoutOrganization = await SellAsync(env, grant, At(11), user: removed);
        var withoutRole = await SellAsync(env, grant, At(11), user: noRole);

        Assert.False(withoutOrganization.IsSuccess);
        Assert.False(withoutRole.IsSuccess);
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(env));
    }

    [Fact]
    public async Task EachReplayedOperation_IsCheckedAgainstItsOwnCurrentPermission()
    {
        // Every role holds both SalesCreate and PaymentsRecord today, so the separation is shown on the role with neither (no
        // membership) and on the handlers' own checks: a grant is held by a user allowed either, and authorizes neither.
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);
        var sale = (await SellAsync(env, null, null, onAccount: true, amountPaid: 0m)).Value.Sale.Id;
        var noRole = new FakeUser(env.Owner.UserId, env.Organization, null);

        var offlineSale = await SellAsync(env, grant, At(11), user: noRole);
        var offlinePayment = await PayAsync(env, sale, 5m, grant, At(11), user: noRole);

        Assert.Equal(ErrorKind.Forbidden, offlineSale.Error!.Kind);
        Assert.Equal(ErrorKind.Forbidden, offlinePayment.Error!.Kind);
        Assert.Equal((1, 1, 1, 0, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task AStaffCashier_MayHoldAGrantAndReplaySalesAndPayments()
    {
        var env = await NewEnvAsync();
        var cashier = env.As(Role.Staff);
        env.Clock.UtcNow = At(10);
        var grant = (await IssueAsync(env, cashier)).Value.GrantId;
        env.Clock.UtcNow = At(10, 20);
        Assert.True((await ContactAsync(env, grant, cashier)).IsSuccess);
        env.Clock.UtcNow = At(12);

        var sale = await SellAsync(env, grant, At(11), onAccount: true, amountPaid: 0m, user: cashier);
        var payment = await PayAsync(env, sale.Value.Sale.Id, 5m, grant, At(11, 30), user: cashier);

        Assert.True(sale.IsSuccess, sale.Error?.Code);
        Assert.True(payment.IsSuccess, payment.Error?.Code);
        Assert.Equal(cashier.UserId, sale.Value.Sale.CreatedByUserId);
    }

    // ---- price

    [Fact]
    public async Task ACatalogPriceThatChanged_IsAConflict_RecordsNothing_AndReservesNoId()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        await using (var db = database.CreateContext(env.Organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == env.Product);
            product.Update(product.Sku, product.Name, product.UnitOfMeasure, Money.Create(12m, Gmd), product.CostPrice, null, At(11, 30));
            await db.SaveChangesAsync();
        }

        env.Clock.UtcNow = At(12);
        var saleId = Guid.CreateVersion7();

        var drifted = await SellAsync(env, grant, At(11), saleId, shown: 10m);

        Assert.Equal("sale.catalog_price_changed", drifted.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, drifted.Error.Kind);
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(env));
        var resubmitted = await SellAsync(env, grant, At(11), saleId, shown: 12m);
        Assert.True(resubmitted.IsSuccess, resubmitted.Error?.Code);
        Assert.Equal(24m, resubmitted.Value.Sale.Total);
    }

    [Fact]
    public async Task AnAuthorizedOverride_IsNotCatalogDrift_BecauseOnlyTheCatalogPriceIsCompared()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);

        var negotiated = await SellAsync(env, grant, At(11), shown: 10m, unitPrice: 8m, reason: "Negotiated with the customer");

        Assert.True(negotiated.IsSuccess, negotiated.Error?.Code);
        var line = negotiated.Value.Sale.Lines.Single();
        Assert.Equal((10m, 8m, "Negotiated with the customer"), (line.CatalogUnitPrice, line.UnitPrice, line.PriceOverrideReason));
    }

    [Fact]
    public async Task AnOfflineSale_MustStateTheCatalogPriceItSaw_OnEveryLine()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(12);

        var result = await SellAsync(env, grant, At(11), shown: null);

        Assert.Equal("sale.catalog_price_required", result.Error!.Code);
    }

    [Fact]
    public async Task AnOnlineSale_IsUnchanged_ItHappensNowAndWasReceivedNow()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(12);

        var result = await SellAsync(env, null, null, shown: null);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal((At(12), At(12), null), (result.Value.Sale.CreatedAtUtc, result.Value.Sale.ReceivedAtUtc, result.Value.Sale.OfflineGrantId));
        Assert.Equal((1, 1, 1, 0, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task ACatalogChange_IsDrift_EvenWhenTheFinalPriceIsAnAuthorizedOverride()
    {
        // The catalog was 10 and the cashier saw 10 and charged an authorized 8; the catalog became 12 while offline.
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        await using (var db = database.CreateContext(env.Organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == env.Product);
            product.Update(product.Sku, product.Name, product.UnitOfMeasure, Money.Create(12m, Gmd), product.CostPrice, null, At(11, 30));
            await db.SaveChangesAsync();
        }

        env.Clock.UtcNow = At(12);

        var drifted = await SellAsync(env, grant, At(11), shown: 10m, unitPrice: 8m, reason: "Negotiated");
        var seenCurrent = await SellAsync(env, grant, At(11), shown: 12m, unitPrice: 8m, reason: "Negotiated");

        Assert.Equal("sale.catalog_price_changed", drifted.Error!.Code); // the drift is the catalog moving from what was seen, not the override
        Assert.True(seenCurrent.IsSuccess, seenCurrent.Error?.Code);
        Assert.Equal((12m, 8m), (seenCurrent.Value.Sale.Lines.Single().CatalogUnitPrice, seenCurrent.Value.Sale.Lines.Single().UnitPrice));
    }

    [Fact]
    public async Task AContactAfterTheFact_CannotCreateAnOldGap_BecauseItIsRecordedAtTheServersReceiptTime()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(10);
        var grant = (await IssueAsync(env)).Value.GrantId;
        for (var minute = 5; minute <= 120; minute += 5)
        {
            env.Clock.UtcNow = At(10).AddMinutes(minute);
            Assert.True((await ContactAsync(env, grant)).IsSuccess);
        }

        // Silent from 12:00; the attacker reports contact at Tuesday 15:00 and then claims Monday 11:00.
        env.Clock.UtcNow = At(15, day: 1);
        var contact = await ContactAsync(env, grant);
        var attack = await SellAsync(env, grant, At(11));
        var honestSilence = await SellAsync(env, grant, At(14));

        Assert.Equal(At(15, day: 1), contact.Value.LastContactAtUtc);
        Assert.Equal("offline.event_time_unverifiable", attack.Error!.Code);
        Assert.True(honestSilence.IsSuccess, honestSilence.Error?.Code);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(At(15, day: 1), await db.Set<OfflineContact>().Where(c => c.GrantId == grant).MaxAsync(c => c.ObservedAtUtc));
    }

    [Fact]
    public async Task AnOnlinePaymentAndAnOnlineSale_AreAdditivelyUnchanged_NoGrantNoEventTime()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(12);
        var sale = (await SellAsync(env, null, null, shown: null, onAccount: true, amountPaid: 0m)).Value.Sale;
        env.Clock.UtcNow = At(12, 5);

        var payment = await PayAsync(env, sale.Id, 5m, null, null);
        var datedPayment = await PayAsync(env, sale.Id, 5m, null, At(12, 1));
        var tooEarly = await PayAsync(env, sale.Id, 5m, null, At(11, 59));

        Assert.True(payment.IsSuccess, payment.Error?.Code);
        Assert.True(datedPayment.IsSuccess, datedPayment.Error?.Code);
        Assert.Equal("sale.payment_before_sale", tooEarly.Error!.Code);
        Assert.Equal(At(12, 5), payment.Value.Sale.Payments.Max(p => p.RecordedAtUtc));
        Assert.Equal((1, 1, 1, 0, 0, 98m), await StateAsync(env));
    }

    // ---- idempotent replay

    [Fact]
    public async Task ALostResponse_FollowedByRetries_HasExactlyOneBusinessEffect()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();

        var first = await SellAsync(env, grant, At(11), saleId);
        // The response is lost; the client retries, after a catalog price change and a later contact, five times.
        await using (var db = database.CreateContext(env.Organization))
        {
            var product = await db.Set<Product>().SingleAsync(p => p.Id == env.Product);
            product.Update(product.Sku, product.Name, product.UnitOfMeasure, Money.Create(15m, Gmd), product.CostPrice, null, At(11, 4));
            await db.SaveChangesAsync();
        }

        env.Clock.UtcNow = At(11, 10);
        Assert.True((await ContactAsync(env, grant)).IsSuccess);
        var retries = new List<Result<SaleResultDto>>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            retries.Add(await SellAsync(env, grant, At(11), saleId));
        }

        Assert.False(first.Value.AlreadyRecorded);
        Assert.All(retries, retry =>
        {
            Assert.True(retry.IsSuccess, retry.Error?.Code);
            Assert.True(retry.Value.AlreadyRecorded);
            Assert.Equal(first.Value.Sale.Total, retry.Value.Sale.Total);
        });
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task TheSameIdWithADifferentRequest_IsAConflict_AndChangesNothing()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();
        Assert.True((await SellAsync(env, grant, At(11), saleId)).IsSuccess);

        var otherTime = await SellAsync(env, grant, At(11, 1), saleId);
        var otherQuantity = await SellAsync(env, grant, At(11), saleId, quantity: 3m);
        var asOnline = await SellAsync(env, null, null, saleId);

        Assert.Equal("sale.id_conflict", otherTime.Error!.Code);
        Assert.Equal("sale.id_conflict", otherQuantity.Error!.Code);
        Assert.Equal("sale.id_conflict", asOnline.Error!.Code);
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task FiveSimultaneousReplaysOfTheSameSale_HaveExactlyOneBusinessEffect()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SellAsync(env, grant, At(11), saleId)));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Code));
        Assert.Equal(1, results.Count(r => !r.Value.AlreadyRecorded));
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task ARefusedSale_ThenACorrectedOne_UnderTheSameId_Succeeds()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();

        var refusedForQuantity = await SellAsync(env, grant, At(11), saleId, quantity: 1000m);
        var refusedForPrice = await SellAsync(env, grant, At(11), saleId, shown: 9m);

        Assert.Equal("stock_level.insufficient_stock", refusedForQuantity.Error!.Code);
        Assert.Equal("sale.catalog_price_changed", refusedForPrice.Error!.Code);
        Assert.Equal((0, 0, 0, 0, 0, 100m), await StateAsync(env)); // a failure after the event time was vouched leaves no accepted audit event behind

        var corrected = await SellAsync(env, grant, At(11), saleId);
        Assert.True(corrected.IsSuccess, corrected.Error?.Code);
        Assert.Equal((1, 1, 1, 1, 0, 98m), await StateAsync(env));
    }

    [Fact]
    public async Task InsufficientAuthoritativeStock_IsAConflictForTheServerToDecide_NothingIsManufactured()
    {
        var env = await NewEnvAsync(stock: 3m);
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);

        var first = await SellAsync(env, grant, At(11), quantity: 2m);
        var second = await SellAsync(env, grant, At(11, 1), quantity: 2m);

        Assert.True(first.IsSuccess);
        Assert.Equal("stock_level.insufficient_stock", second.Error!.Code);
        Assert.Equal((1, 1, 1, 1, 0, 1m), await StateAsync(env));
    }

    // ---- payments

    [Fact]
    public async Task AnOfflinePayment_IsAcceptedInsideAGap_IsIdempotent_AndAuditsItsProvenance()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var sale = (await SellAsync(env, grant, At(11), onAccount: true, amountPaid: 0m)).Value.Sale;
        var paymentId = Guid.CreateVersion7();

        var first = await PayAsync(env, sale.Id, 8m, grant, At(11, 1), paymentId);
        var retries = new List<Result<PaymentResultDto>>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            retries.Add(await PayAsync(env, sale.Id, 8m, grant, At(11, 1), paymentId));
        }

        Assert.True(first.IsSuccess, first.Error?.Code);
        Assert.False(first.Value.AlreadyRecorded);
        Assert.All(retries, r => Assert.True(r.Value.AlreadyRecorded));
        Assert.Equal(8m, first.Value.Sale.AmountPaid);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.Id == paymentId));
        var audit = await db.Set<AuditEvent>().SingleAsync(e => e.Action == AuditActions.OfflinePaymentAccepted);
        Assert.Contains(grant.ToString("D"), audit.Description!);
        Assert.DoesNotContain("Awa", audit.Description!);
    }

    [Fact]
    public async Task ABackdatedOfflinePayment_IsRefused_ReservesNoId_AndOverpaymentStaysRefused()
    {
        var env = await NewEnvAsync();
        env.Clock.UtcNow = At(10);
        var grant = (await IssueAsync(env)).Value.GrantId;
        env.Clock.UtcNow = At(10, 1);
        var sale = (await SellAsync(env, null, null, onAccount: true, amountPaid: 0m)).Value.Sale; // 20 owed, sold at 10:01
        for (var minute = 5; minute <= 60; minute += 5)
        {
            env.Clock.UtcNow = At(10).AddMinutes(minute);
            Assert.True((await ContactAsync(env, grant)).IsSuccess);
        }

        env.Clock.UtcNow = At(15);
        var paymentId = Guid.CreateVersion7();

        var backdated = await PayAsync(env, sale.Id, 5m, grant, At(10, 30), paymentId);
        var noTime = await PayAsync(env, sale.Id, 5m, grant, null);
        var overpay = await PayAsync(env, sale.Id, 21m, grant, At(12));
        var ok = await PayAsync(env, sale.Id, 5m, grant, At(12), paymentId);

        Assert.Equal("offline.event_time_unverifiable", backdated.Error!.Code);
        Assert.Equal("offline.event_time_required", noTime.Error!.Code);
        Assert.Equal("sale.payment_exceeds_outstanding", overpay.Error!.Code);
        Assert.True(ok.IsSuccess, ok.Error?.Code);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(1, await db.Set<SalePayment>().CountAsync(p => p.Kind == SalePaymentKind.Payment && p.Id == paymentId));
    }

    [Fact]
    public async Task AnOfflinePaymentWithADifferentRequestUnderTheSameId_IsAConflict()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var sale = (await SellAsync(env, grant, At(11), onAccount: true, amountPaid: 0m)).Value.Sale;
        var paymentId = Guid.CreateVersion7();
        Assert.True((await PayAsync(env, sale.Id, 5m, grant, At(11, 1), paymentId)).IsSuccess);

        var different = await PayAsync(env, sale.Id, 6m, grant, At(11, 1), paymentId);

        Assert.Equal("sale.payment_id_conflict", different.Error!.Code);
    }

    [Fact]
    public async Task ConcurrentOfflinePayments_NeverPayMoreThanIsOwed()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var sale = (await SellAsync(env, grant, At(11), onAccount: true, amountPaid: 0m)).Value.Sale; // 20 owed

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => PayAsync(env, sale.Id, 8m, grant, At(11, 1))));

        Assert.Equal(2, results.Count(r => r.IsSuccess));
        await using var db = database.CreateContext(env.Organization);
        var stored = await db.Set<Sale>().Include(s => s.Lines).Include(s => s.Payments).SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(16m, stored.AmountPaid.Amount);
        Assert.True(stored.Outstanding.Amount >= 0m);
    }

    // ---- audit

    [Fact]
    public async Task TheAuditTrail_HoldsTheFactsOfARefusalAndOfAnAcceptance_AndNothingPersonal()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        await SellAsync(env, grant, At(9));
        await SellAsync(env, grant, At(11), onAccount: true, amountPaid: 0m);

        await using var db = database.CreateContext(env.Organization);
        var refused = await db.Set<AuditEvent>().SingleAsync(e => e.Action == AuditActions.OfflineEventRefused);
        var accepted = await db.Set<AuditEvent>().SingleAsync(e => e.Action == AuditActions.OfflineSaleAccepted);

        Assert.Equal((grant, env.Owner.UserId), (refused.EntityId, refused.ActorId));
        Assert.Contains("offline.event_time_unverifiable", refused.Description!);
        Assert.Contains(grant.ToString("D"), accepted.Description!);
        Assert.Contains("silent gap", accepted.Description!);
        Assert.Contains("delay 180 s", accepted.Description!);
        Assert.DoesNotContain("Awa", accepted.Description!);
    }

    // ---- pruning

    [Fact]
    public async Task OldEvidence_IsPrunedOnlyAfterItCanNoLongerVouchForAnythingPlusTheRetention()
    {
        var env = await NewEnvAsync();
        var other = await NewEnvAsync();
        var first = (await IssueAsync(env)).Value.GrantId;
        var foreign = (await IssueAsync(other)).Value.GrantId;
        env.Clock.UtcNow = Monday10.AddHours(1);
        var second = (await IssueAsync(env)).Value.GrantId; // supersedes the first at 11:00: it can vouch up to 11:00
        var boundary = Monday10.AddHours(1).AddDays(90);

        env.Clock.UtcNow = boundary;
        var third = (await IssueAsync(env)).Value.GrantId;
        Assert.Equal(3, await CountGrantsAsync(env, first, second, third));

        env.Clock.UtcNow = boundary.AddTicks(10);
        var fourth = (await IssueAsync(env)).Value.GrantId;
        Assert.Equal(3, await CountGrantsAsync(env, first, second, third, fourth)); // the first is gone, the other three remain

        await using var db = database.CreateContext(env.Organization);
        Assert.False(await db.Set<OfflineGrant>().AnyAsync(g => g.Id == first));
        Assert.Equal(0, await db.Set<OfflineContact>().CountAsync(c => c.GrantId == first));
        await using var otherDb = database.CreateContext(other.Organization);
        Assert.True(await otherDb.Set<OfflineGrant>().AnyAsync(g => g.Id == foreign)); // another organization's evidence is never touched
        Assert.True(foreign != Guid.Empty);
    }

    [Fact]
    public async Task APrunedGrant_LeavesTheSalesItVouchedForIntact_AndALateReplayIsRefusedAsUnknown()
    {
        var env = await NewEnvAsync();
        var grant = await GrantWithContactUntil1015Async(env);
        env.Clock.UtcNow = At(11, 3);
        var saleId = Guid.CreateVersion7();
        Assert.True((await SellAsync(env, grant, At(11), saleId)).IsSuccess);
        env.Clock.UtcNow = Monday10.AddDays(200);
        Assert.True((await IssueAsync(env)).IsSuccess); // a new grant supersedes the old one and prunes it

        var sale = await LoadSaleAsync(env, saleId);
        var late = await SellAsync(env, grant, At(11), Guid.CreateVersion7());

        Assert.Equal(grant, sale.OfflineGrantId);
        Assert.Equal("offline.grant_not_found", late.Error!.Code);
        // The repeat of the already recorded sale does not need the pruned evidence, and the audit trail is not pruned.
        var repeat = await SellAsync(env, grant, At(11), saleId);
        Assert.True(repeat.IsSuccess, repeat.Error?.Code);
        Assert.True(repeat.Value.AlreadyRecorded);
        await using var db = database.CreateContext(env.Organization);
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.OfflineSaleAccepted && e.EntityId == saleId));
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.OfflineGrantIssued && e.EntityId == grant));
    }

    private async Task<Sale> LoadSaleAsync(Env env, Guid id)
    {
        await using var db = database.CreateContext(env.Organization);
        return await db.Set<Sale>().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task<int> CountGrantsAsync(Env env, params Guid[] ids)
    {
        await using var db = database.CreateContext(env.Organization);
        return await db.Set<OfflineGrant>().CountAsync(g => ids.Contains(g.Id));
    }
}
