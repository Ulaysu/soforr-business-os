using Microsoft.EntityFrameworkCore;
using Soforr.Api.IntegrationTests.Postgres;
using Soforr.Application.Audit;
using Soforr.Application.Audit.ListAuditEvents;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Domain.Audit;
using Soforr.Domain.Common;
using Soforr.Domain.Identity;
using Soforr.Domain.Organizations;

namespace Soforr.Api.IntegrationTests.Audit;

/// <summary>The audit trail query on real PostgreSQL: who may read it, what it returns, in what order, and for whom.</summary>
[Trait("Category", "Integration")]
public sealed class ListAuditEventsHandlerTests(PostgresDatabaseFixture database) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser In(Guid organizationId, Role role) => new(Guid.CreateVersion7(), organizationId, role);
    }

    private async Task<Guid> NewOrganizationAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = database.CreateContext();
        db.Set<Organization>().Add(Organization.Create(id, "Soforr Trading", Currency.Create("GMD"), "UTC", Start));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<AuditEvent> AddAsync(Guid organization, string action, int minute, Guid? actor = null, string? entityType = null, Guid? entityId = null)
    {
        var auditEvent = AuditEvent.Record(Guid.CreateVersion7(), organization, actor, action, entityType, entityId, null, "corr", Start.AddMinutes(minute));
        await using var db = database.CreateContext(organization);
        db.Set<AuditEvent>().Add(auditEvent);
        await db.SaveChangesAsync();
        return auditEvent;
    }

    private async Task<Result<PagedResult<AuditEventDto>>> ListAsync(Guid organization, ListAuditEventsQuery query, Role role = Role.Owner)
    {
        await using var db = database.CreateContext(organization);
        return await new ListAuditEventsHandler(db, FakeUser.In(organization, role)).HandleAsync(query, CancellationToken.None);
    }

    private static ListAuditEventsQuery Query(string? action = null, string? entityType = null, Guid? entityId = null, Guid? actorId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int page = 1, int pageSize = 25) =>
        new(action, entityType, entityId, actorId, from, to, page, pageSize);

    [Theory]
    [InlineData(Role.Owner, true)]
    [InlineData(Role.Manager, false)]
    [InlineData(Role.Staff, false)]
    public async Task OnlyTheOwner_MayReadTheAuditTrail(Role role, bool allowed)
    {
        var organization = await NewOrganizationAsync();
        await AddAsync(organization, "auth.sign_in_succeeded", 0);

        var result = await ListAsync(organization, Query(), role);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal("identity.forbidden", result.Error!.Code);
        }
    }

    [Fact]
    public async Task WithoutBeingSignedIn_TheTrailIsNotAuthenticated()
    {
        var organization = await NewOrganizationAsync();
        await using var db = database.CreateContext(organization);

        var result = await new ListAuditEventsHandler(db, new FakeUser(null, null, null)).HandleAsync(Query(), CancellationToken.None);

        Assert.Equal("identity.not_authenticated", result.Error!.Code);
    }

    [Fact]
    public async Task TheTrail_IsNewestFirst_WithAStableOrderForEqualTimes_AndTheTotal()
    {
        var organization = await NewOrganizationAsync();
        await AddAsync(organization, "a.first", 0);
        await AddAsync(organization, "a.third", 2);
        var tieOne = await AddAsync(organization, "a.second", 1);
        var tieTwo = await AddAsync(organization, "a.second", 1);

        var result = await ListAsync(organization, Query());

        var expectedTie = new[] { tieOne.Id, tieTwo.Id }.OrderByDescending(id => id).ToArray();
        Assert.Equal("a.third", result.Value.Items[0].Action);
        Assert.Equal(expectedTie, result.Value.Items.Skip(1).Take(2).Select(item => item.Id).ToArray());
        Assert.Equal("a.first", result.Value.Items[3].Action);
        Assert.Equal(4, result.Value.TotalCount);
    }

    [Fact]
    public async Task Paging_ReturnsTheRequestedPage_AndRejectsOutOfRangeValues()
    {
        var organization = await NewOrganizationAsync();
        for (var minute = 0; minute < 5; minute++)
        {
            await AddAsync(organization, "a.event", minute);
        }

        var second = await ListAsync(organization, Query(page: 2, pageSize: 2));
        var beyond = await ListAsync(organization, Query(page: 4, pageSize: 2));
        var badPage = await ListAsync(organization, Query(page: 0));
        var badSize = await ListAsync(organization, Query(pageSize: 101));

        Assert.Equal([Start.AddMinutes(2), Start.AddMinutes(1)], second.Value.Items.Select(item => item.OccurredAtUtc).ToArray());
        Assert.Equal(5, second.Value.TotalCount);
        Assert.Empty(beyond.Value.Items);
        Assert.Equal("pagination.page_invalid", badPage.Error!.Code);
        Assert.Equal("page", badPage.Error.Field);
        Assert.Equal("pagination.page_size_invalid", badSize.Error!.Code);
        Assert.Equal(ErrorKind.Validation, badSize.Error.Kind);
    }

    [Fact]
    public async Task Filters_NarrowTheTrail_ByActionActorRecordAndTimeRange()
    {
        var organization = await NewOrganizationAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var record = Guid.CreateVersion7();
        await AddAsync(organization, "auth.sign_in_succeeded", 0, alice, "User", alice);
        await AddAsync(organization, "auth.sign_in_succeeded", 10, bob, "User", bob);
        await AddAsync(organization, "membership.role_assigned", 20, alice, "Membership", record);
        await AddAsync(organization, "auth.signed_out", 30, alice, "User", alice);

        var byAction = await ListAsync(organization, Query(action: "auth.sign_in_succeeded"));
        var byActor = await ListAsync(organization, Query(actorId: alice));
        var byRecord = await ListAsync(organization, Query(entityType: "Membership", entityId: record));
        var byRange = await ListAsync(organization, Query(from: Start.AddMinutes(10), to: Start.AddMinutes(30)));
        var combined = await ListAsync(organization, Query(action: "auth.sign_in_succeeded", actorId: alice));
        var none = await ListAsync(organization, Query(action: "nothing.here"));

        Assert.Equal(2, byAction.Value.TotalCount);
        Assert.Equal(3, byActor.Value.TotalCount);
        Assert.Equal(["membership.role_assigned"], byRecord.Value.Items.Select(item => item.Action).ToArray());
        Assert.Equal(["membership.role_assigned", "auth.sign_in_succeeded"], byRange.Value.Items.Select(item => item.Action).ToArray());
        Assert.Equal(1, combined.Value.TotalCount);
        Assert.Equal(0, none.Value.TotalCount);
    }

    [Fact]
    public async Task ARangeThatEndsBeforeItStarts_IsAValidationError()
    {
        var organization = await NewOrganizationAsync();

        var result = await ListAsync(organization, Query(from: Start.AddMinutes(5), to: Start.AddMinutes(5)));

        Assert.Equal("audit.range_invalid", result.Error!.Code);
        Assert.Equal("from", result.Error.Field);
    }

    [Fact]
    public async Task TheDtoCarriesTheFields_AndNeverTheEntity()
    {
        var organization = await NewOrganizationAsync();
        var actor = Guid.CreateVersion7();
        var record = Guid.CreateVersion7();
        var added = await AddAsync(organization, "membership.role_assigned", 0, actor, "Membership", record);

        var result = await ListAsync(organization, Query());

        var item = Assert.Single(result.Value.Items);
        Assert.Equal(new AuditEventDto(added.Id, "membership.role_assigned", actor, "Membership", record, null, "corr", Start), item);
    }

    [Fact]
    public async Task TheTrail_ContainsOnlyTheCallersOrganization()
    {
        var mine = await NewOrganizationAsync();
        var theirs = await NewOrganizationAsync();
        await AddAsync(mine, "a.mine", 0);
        await AddAsync(theirs, "a.theirs", 0);

        var result = await ListAsync(mine, Query());

        Assert.Equal(["a.mine"], result.Value.Items.Select(item => item.Action).ToArray());
        Assert.Equal(1, result.Value.TotalCount);
    }
}
