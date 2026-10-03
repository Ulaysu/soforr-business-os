using Microsoft.EntityFrameworkCore;
using Soforr.Application.Catalog;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Inventory;

namespace Soforr.Application.Inventory.GetMovementHistory;

public sealed record GetMovementHistoryQuery(Guid ProductId, int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 1_000_000;
}

/// <summary>One recorded change to a product's stock. <see cref="QuantityChange"/> is signed: positive added stock, negative removed it.</summary>
public sealed record MovementDto(
    Guid Id,
    decimal QuantityChange,
    string Reason,
    string SourceType,
    Guid SourceId,
    Guid ActorId,
    string? Note,
    DateTimeOffset OccurredAtUtc)
{
    internal static MovementDto From(InventoryMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return new MovementDto(
            movement.Id,
            movement.QuantityDelta.Value,
            movement.Reason.ToString(),
            movement.SourceType.ToString(),
            movement.SourceId,
            movement.ActorId,
            movement.Note,
            movement.OccurredAtUtc);
    }
}

/// <summary>
/// The ledger of one product, newest first. It shows who changed the stock, when and why, so it needs its own
/// permission (<c>InventoryViewHistory</c>), granted to Owner and Manager.
/// </summary>
public sealed class GetMovementHistoryHandler(IAppDbContext db, ICurrentUser user) : IQueryHandler<GetMovementHistoryQuery, PagedResult<MovementDto>>
{
    public async Task<Result<PagedResult<MovementDto>>> HandleAsync(GetMovementHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (user.Require(Permission.InventoryViewHistory) is { } denied)
        {
            return Result.Failure<PagedResult<MovementDto>>(denied);
        }

        if (query.Page is < 1 or > GetMovementHistoryQuery.MaxPage)
        {
            return Fail("pagination.page_invalid", "page", $"The page must be between 1 and {GetMovementHistoryQuery.MaxPage}.");
        }

        if (query.PageSize is < 1 or > GetMovementHistoryQuery.MaxPageSize)
        {
            return Fail("pagination.page_size_invalid", "pageSize", $"The page size must be between 1 and {GetMovementHistoryQuery.MaxPageSize}.");
        }

        // Filtered to the caller's organization: another organization's product is not found.
        if (!await db.Set<Product>().AnyAsync(p => p.Id == query.ProductId, cancellationToken))
        {
            return Result.Failure<PagedResult<MovementDto>>(ProductErrors.NotFound(query.ProductId));
        }

        var movements = db.Set<InventoryMovement>().AsNoTracking().Where(movement => movement.ProductId == query.ProductId);
        var total = await movements.CountAsync(cancellationToken);
        var skip = (int)((long)(query.Page - 1) * query.PageSize);

        var page = await movements
            .OrderByDescending(movement => movement.OccurredAtUtc)
            .ThenByDescending(movement => movement.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<MovementDto>(page.Select(MovementDto.From).ToList(), query.Page, query.PageSize, total));
    }

    private static Result<PagedResult<MovementDto>> Fail(string code, string field, string message) =>
        Result.Failure<PagedResult<MovementDto>>(new Error(code, ErrorKind.Validation, message, field));
}
