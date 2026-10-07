using Microsoft.Extensions.DependencyInjection;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Intelligence;
using Soforr.Application.Intelligence.GetInsights;
using Soforr.Application.Intelligence.Sources;
using Soforr.Domain.Identity;

namespace Soforr.Application.Tests.Intelligence;

public class GetInsightsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Someone = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c3a01");
    private static readonly Guid SomeOrganization = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c3a02");

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeUser(Guid? userId, Guid? organizationId, Role? role) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;

        public Guid? OrganizationId { get; } = organizationId;

        public Role? Role { get; } = role;

        public static FakeUser As(Role role) => new(Someone, SomeOrganization, role);
    }

    private sealed class FakeSource(bool requiresCost, params Insight[] insights) : IInsightSource
    {
        public bool RequiresProductViewCost { get; } = requiresCost;

        public int Calls { get; private set; }

        public InsightContext? SeenContext { get; private set; }

        public Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken)
        {
            Calls++;
            SeenContext = context;
            return Task.FromResult<IReadOnlyList<Insight>>(insights);
        }
    }

    private sealed class FailingSource : IInsightSource
    {
        public bool RequiresProductViewCost => false;

        public Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a source failed");
    }

    private static Insight Make(string type, InsightSeverity severity, string name, Guid? id = null) =>
        Insight.Create(type, severity, new InsightSubject("Product", id ?? Guid.CreateVersion7(), name), [InsightFact.Text("k", "v")], "Because.", new InsightAction("act", "Do it"));

    private static GetInsightsHandler Handler(ICurrentUser user, params IInsightSource[] sources) =>
        new(sources, new FixedClock(Now), user);

    [Fact]
    public async Task TheInsights_AreOrdered_BySeverityThenTypeThenSubjectNameThenId()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var source = new FakeSource(
            false,
            Make("b.type", InsightSeverity.Low, "Zed"),
            Make("a.type", InsightSeverity.Low, "Zed"),
            Make("a.type", InsightSeverity.High, "Mid"),
            Make("a.type", InsightSeverity.Medium, "Bee"),
            Make("a.type", InsightSeverity.Medium, "Ant"),
            Make("a.type", InsightSeverity.Medium, "Same", secondId),
            Make("a.type", InsightSeverity.Medium, "Same", firstId),
            Make("z.type", InsightSeverity.Info, "Note"));

        var result = await Handler(FakeUser.As(Role.Owner), source).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, result.Value.EvaluatedAtUtc);
        Assert.Equal(
            [("a.type", "High", "Mid"), ("a.type", "Medium", "Ant"), ("a.type", "Medium", "Bee"), ("a.type", "Medium", "Same"), ("a.type", "Medium", "Same"), ("a.type", "Low", "Zed"), ("b.type", "Low", "Zed"), ("z.type", "Info", "Note")],
            result.Value.Items.Select(i => (i.Type, i.Severity, i.Subject.Name)).ToArray());
        Assert.Equal([firstId, secondId], result.Value.Items.Where(i => i.Subject.Name == "Same").Select(i => i.Subject.Id!.Value).ToArray());
    }

    [Fact]
    public async Task TheCombinedResult_DoesNotDependOnTheOrderOfTheSourcesOrTheirInsights()
    {
        var one = Make("a.type", InsightSeverity.High, "One");
        var two = Make("b.type", InsightSeverity.High, "Two");
        var three = Make("c.type", InsightSeverity.Low, "Three");

        var first = await Handler(FakeUser.As(Role.Owner), new FakeSource(false, one, three), new FakeSource(false, two)).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
        var second = await Handler(FakeUser.As(Role.Owner), new FakeSource(false, two), new FakeSource(false, three, one)).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.Equal(first.Value.Items.Select(i => i.Key), second.Value.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task EachSource_IsGivenTheClock_AndWhetherTheCallerMaySeeCosts()
    {
        var source = new FakeSource(false);

        await Handler(FakeUser.As(Role.Owner), source).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.Equal(new InsightContext(Now, CanSeeCosts: true), source.SeenContext);
    }

    [Theory]
    [InlineData(Role.Owner)]
    [InlineData(Role.Manager)]
    public async Task ACostSource_RunsForEveryRoleThatMaySeeInsights_BecauseTheyAllHoldProductViewCost(Role role)
    {
        // Gate under test: the handler runs a cost source only when the caller holds ProductViewCost. Today every role that
        // holds InsightsView also holds ProductViewCost, so the "skipped" half cannot be exercised through a real role yet;
        // it gets its own test the day a role holds InsightsView without ProductViewCost (see docs/engineering/intelligence.md).
        var plain = new FakeSource(false, Make("inventory.x", InsightSeverity.Low, "Plain"));
        var cost = new FakeSource(true, Make("margin.x", InsightSeverity.High, "Costly"));

        var result = await Handler(FakeUser.As(role), plain, cost).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.Equal(["Costly", "Plain"], result.Value.Items.Select(i => i.Subject.Name).ToArray());
        Assert.Equal(1, cost.Calls);
        Assert.True(cost.SeenContext!.CanSeeCosts);
    }

    [Fact]
    public async Task WithNoSources_OrNoInsights_TheResultIsEmpty_NotAnError()
    {
        var none = await Handler(FakeUser.As(Role.Owner)).HandleAsync(new GetInsightsQuery(), CancellationToken.None);
        var quiet = await Handler(FakeUser.As(Role.Owner), new FakeSource(false)).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(none.IsSuccess);
        Assert.Empty(none.Value.Items);
        Assert.Equal(Now, none.Value.EvaluatedAtUtc);
        Assert.True(quiet.IsSuccess);
        Assert.Empty(quiet.Value.Items);
    }

    [Fact]
    public async Task AFailingSource_FailsTheWholeEvaluation_InsteadOfSilentlyOmittingInsights()
    {
        var handler = Handler(FakeUser.As(Role.Owner), new FakeSource(false, Make("a.x", InsightSeverity.Low, "Fine")), new FailingSource());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new GetInsightsQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task ASignedOutCaller_IsUnauthenticated_AndNoSourceRuns()
    {
        var source = new FakeSource(false);

        var result = await Handler(new FakeUser(null, null, null), source).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unauthenticated, result.Error!.Kind);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AStaffCaller_IsForbidden_AndNoSourceRuns()
    {
        var source = new FakeSource(false);

        var result = await Handler(FakeUser.As(Role.Staff), source).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task ACallerWithoutAMembership_IsForbidden()
    {
        var result = await Handler(new FakeUser(Someone, null, null), new FakeSource(false)).HandleAsync(new GetInsightsQuery(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public void EverySourceInTheAssembly_IsRegistered_WithoutBeingListedByHand()
    {
        var services = new ServiceCollection();

        services.AddInsightSources(typeof(IInsightSource).Assembly);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IInsightSource) && descriptor.ImplementationType == typeof(InventoryRiskInsightSource));
        Assert.All(services, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
    }

    [Fact]
    public void OnlyConcreteSources_AreRegistered()
    {
        var services = new ServiceCollection();

        services.AddInsightSources([typeof(IInsightSource), typeof(FakeSource), typeof(string), typeof(AbstractSource)]);

        var registered = services.Select(descriptor => descriptor.ImplementationType).ToArray();
        Assert.Equal([typeof(FakeSource)], registered);
    }

    private abstract class AbstractSource : IInsightSource
    {
        public bool RequiresProductViewCost => false;

        public abstract Task<IReadOnlyList<Insight>> EvaluateAsync(InsightContext context, CancellationToken cancellationToken);
    }
}
