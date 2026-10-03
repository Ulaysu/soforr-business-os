using Soforr.Domain.Audit;
using Soforr.Domain.Common;

namespace Soforr.Domain.Tests.Audit;

public class AuditEventTests
{
    private static readonly Guid Id = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c4a01");
    private static readonly Guid OrganizationId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c4a02");
    private static readonly Guid ActorId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c4a03");
    private static readonly Guid EntityId = Guid.Parse("0197f6a2-3b1c-7c1e-9d2a-5e8f4b6c4a04");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent Record(
        string? action = "auth.sign_in_succeeded",
        Guid? actor = null,
        string? entityType = "User",
        Guid? entityId = null,
        string? description = null,
        string? correlationId = "abc123",
        Guid? id = null,
        Guid? organizationId = null,
        DateTimeOffset? at = null) =>
        AuditEvent.Record(id ?? Id, organizationId ?? OrganizationId, actor ?? ActorId, action, entityType, entityType is null ? null : entityId ?? EntityId, description, correlationId, at ?? Now);

    [Fact]
    public void Record_KeepsEveryField()
    {
        var auditEvent = Record(description: "  Role Owner assigned.  ");

        Assert.Equal(Id, auditEvent.Id);
        Assert.Equal(OrganizationId, auditEvent.OrganizationId);
        Assert.Equal(ActorId, auditEvent.ActorId);
        Assert.Equal("auth.sign_in_succeeded", auditEvent.Action);
        Assert.Equal("User", auditEvent.EntityType);
        Assert.Equal(EntityId, auditEvent.EntityId);
        Assert.Equal("Role Owner assigned.", auditEvent.Description);
        Assert.Equal("abc123", auditEvent.CorrelationId);
        Assert.Equal(Now, auditEvent.OccurredAtUtc);
    }

    [Fact]
    public void AnEventWithoutAnActorOrAReference_IsAllowed_ForSomethingTheSystemDid()
    {
        var auditEvent = AuditEvent.Record(Id, OrganizationId, actorId: null, "system.job_ran", entityType: null, entityId: null, description: null, correlationId: null, Now);

        Assert.Null(auditEvent.ActorId);
        Assert.Null(auditEvent.EntityType);
        Assert.Null(auditEvent.EntityId);
        Assert.Null(auditEvent.Description);
        Assert.Null(auditEvent.CorrelationId);
    }

    [Theory]
    [InlineData("user.registered")]
    [InlineData("membership.role_assigned")]
    [InlineData("a.b")]
    [InlineData("inventory.stock_adjusted.v2")]
    public void ValidActions_AreAccepted(string action) => Assert.Equal(action, Record(action).Action);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("signin")]
    [InlineData("Auth.SignIn")]
    [InlineData("auth.")]
    [InlineData(".sign_in")]
    [InlineData("auth..sign_in")]
    [InlineData("auth.sign-in")]
    [InlineData("auth.sign in")]
    [InlineData("1auth.sign_in")]
    public void InvalidActions_AreRejected(string? action) => AssertRejected("audit_event.action_invalid", () => Record(action));

    [Fact]
    public void AnActionThatIsTooLong_IsRejected() =>
        AssertRejected("audit_event.action_invalid", () => Record("a." + new string('b', AuditEvent.MaxActionLength)));

    [Fact]
    public void AnActionAtTheMaximumLength_IsAccepted() =>
        Assert.Equal(AuditEvent.MaxActionLength, Record("a." + new string('b', AuditEvent.MaxActionLength - 2)).Action.Length);

    [Fact]
    public void AnEventWithoutAnId_IsRejected() => AssertRejected("audit_event.id_required", () => Record(id: Guid.Empty));

    [Fact]
    public void AnEventWithoutAnOrganization_IsRejected() => AssertRejected("audit_event.organization_required", () => Record(organizationId: Guid.Empty));

    [Fact]
    public void AnEmptyActorId_IsRejected_ItMustBeAUserOrAbsent() => AssertRejected("audit_event.actor_invalid", () => Record(actor: Guid.Empty));

    [Fact]
    public void TheEntityTypeAndIdMustBeGivenTogether()
    {
        AssertRejected("audit_event.entity_reference_invalid", () => AuditEvent.Record(Id, OrganizationId, ActorId, "a.b", "User", null, null, null, Now));
        AssertRejected("audit_event.entity_reference_invalid", () => AuditEvent.Record(Id, OrganizationId, ActorId, "a.b", null, EntityId, null, null, Now));
        AssertRejected("audit_event.entity_reference_invalid", () => AuditEvent.Record(Id, OrganizationId, ActorId, "a.b", "User", Guid.Empty, null, null, Now));
    }

    [Fact]
    public void TheLengthsAreBounded()
    {
        AssertRejected("audit_event.entity_type_too_long", () => Record(entityType: new string('t', AuditEvent.MaxEntityTypeLength + 1)));
        AssertRejected("audit_event.description_too_long", () => Record(description: new string('d', AuditEvent.MaxDescriptionLength + 1)));
        AssertRejected("audit_event.correlation_id_too_long", () => Record(correlationId: new string('c', AuditEvent.MaxCorrelationIdLength + 1)));
        Assert.Equal(AuditEvent.MaxDescriptionLength, Record(description: new string('d', AuditEvent.MaxDescriptionLength)).Description!.Length);
    }

    [Fact]
    public void ABlankDescriptionOrCorrelationId_BecomesAbsent()
    {
        var auditEvent = Record(description: "   ", correlationId: "  ");

        Assert.Null(auditEvent.Description);
        Assert.Null(auditEvent.CorrelationId);
    }

    [Fact]
    public void ANonUtcTimestamp_IsRejected() =>
        AssertRejected("audit_event.timestamp_not_utc", () => Record(at: new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(1))));

    [Fact]
    public void TheEvent_HasNoWayToChange_ItsPropertiesHavePrivateSettersAndThereAreNoMutatingMethods()
    {
        var type = typeof(AuditEvent);

        Assert.All(type.GetProperties(), property => Assert.True(property.SetMethod is null || property.SetMethod.IsPrivate, $"{property.Name} must not have a public setter"));
        var publicMethods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();
        Assert.Empty(publicMethods);
    }

    private static void AssertRejected(string code, Func<AuditEvent> create)
    {
        var exception = Assert.Throws<DomainException>(create);
        Assert.Equal(code, exception.Code);
    }
}
