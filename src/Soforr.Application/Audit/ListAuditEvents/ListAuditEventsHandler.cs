using Microsoft.EntityFrameworkCore;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Audit;

namespace Soforr.Application.Audit.ListAuditEvents;

/// <summary>
/// Lists the organization's audit events, newest first, one page at a time. Every filter is optional and exact:
/// <see cref="Action"/> is a full action code, <see cref="EntityType"/> and <see cref="EntityId"/> narrow to one
/// record, <see cref="ActorId"/> to one user, and <see cref="FromUtc"/> (inclusive) and <see cref="ToUtc"/> (exclusive)
/// to a time range.
/// </summary>
public sealed record ListAuditEventsQuery(
    string? Action,
    string? EntityType,
    Guid? EntityId,
    Guid? ActorId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Page,
    int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;
}

/// <summary>Owner only (permission <c>AuditView</c>): the audit trail shows who did what, so it is not for every role.</summary>
public sealed class ListAuditEventsHandler(IAppDbContext db, ICurrentUser user)
    : IQueryHandler<ListAuditEventsQuery, PagedResult<AuditEventDto>>
{
    public async Task<Result<PagedResult<AuditEventDto>>> HandleAsync(ListAuditEventsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.AuditView) is { } denied)
        {
            return Result.Failure<PagedResult<AuditEventDto>>(denied);
        }

        // Out-of-range input is rejected, not silently corrected.
        if (query.Page is < 1 or > ListAuditEventsQuery.MaxPage)
        {
            return Fail("pagination.page_invalid", "page", $"The page must be between 1 and {ListAuditEventsQuery.MaxPage}.");
        }

        if (query.PageSize is < 1 or > ListAuditEventsQuery.MaxPageSize)
        {
            return Fail("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {ListAuditEventsQuery.MaxPageSize}.");
        }

        if (query.FromUtc is { } from && query.ToUtc is { } to && from >= to)
        {
            return Fail("audit.range_invalid", "from", "The start of the range must be before its end.");
        }

        if (query.Action is { Length: > AuditEvent.MaxActionLength } || query.EntityType is { Length: > AuditEvent.MaxEntityTypeLength })
        {
            return Fail("audit.filter_too_long", "action", "A filter value is too long.");
        }

        // Filtered to the caller's organization by the tenant filter.
        var events = db.Set<AuditEvent>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            events = events.Where(auditEvent => auditEvent.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            var entityType = query.EntityType.Trim();
            events = events.Where(auditEvent => auditEvent.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            events = events.Where(auditEvent => auditEvent.EntityId == entityId);
        }

        if (query.ActorId is { } actorId)
        {
            events = events.Where(auditEvent => auditEvent.ActorId == actorId);
        }

        if (query.FromUtc is { } fromUtc)
        {
            events = events.Where(auditEvent => auditEvent.OccurredAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            events = events.Where(auditEvent => auditEvent.OccurredAtUtc < toUtc);
        }

        var total = await events.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);

        var page = await events
            .OrderByDescending(auditEvent => auditEvent.OccurredAtUtc)
            .ThenByDescending(auditEvent => auditEvent.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<AuditEventDto>(page.Select(AuditEventDto.From).ToList(), query.Page, query.PageSize, total));
    }

    private static Result<PagedResult<AuditEventDto>> Fail(string code, string field, string message) =>
        Result.Failure<PagedResult<AuditEventDto>>(new Error(code, ErrorKind.Validation, message, field));
}
