using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Organizations;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Inventory;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.CreateSale;

/// <summary>One product to sell and how much of it. There is no price: the sale uses the product's catalog price at that moment (controlled overrides are SALES-004).</summary>
public sealed record CreateSaleLine(Guid? ProductId, decimal? Quantity);

/// <summary>
/// Records a sale of products. <see cref="SaleId"/> is an optional, client-chosen identifier of this sale: sending the
/// same one again (a retry after a timeout, a double click) changes nothing and reports that it was already recorded,
/// provided it asks for exactly the same products and quantities; the same id with anything different is a conflict.
/// Without an id a new sale is recorded each time.
/// </summary>
public sealed record CreateSaleCommand(Guid? SaleId, IReadOnlyList<CreateSaleLine>? Lines);

/// <summary>
/// The selling use case (ADR 0004). In one transaction it records the sale (with the prices and costs of its products as
/// snapshots), writes one <c>SaleCompleted</c> movement per line through the stock recorder, which removes the stock and
/// refuses a line that would take more than exists, and records the audit event; they are saved together or not at all.
/// The sale id is the sale's key and the movements' source id, so a repeat is recognized by the stored sale (and refused
/// by the unique keys if two arrive together). A conflict with another writer of the same stock is retried by the
/// recorder; each attempt starts from a cleared change tracker and re-validates, so stock that ran out meanwhile is
/// reported as the business error it is.
/// </summary>
public sealed class CreateSaleHandler(IAppDbContext db, StockMovementRecorder recorder, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<CreateSaleCommand, SaleResultDto>
{
    public async Task<Result<SaleResultDto>> HandleAsync(CreateSaleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.SalesCreate) is { } denied)
        {
            return Result.Failure<SaleResultDto>(denied);
        }

        if (command.SaleId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("sale.sale_id_invalid", "saleId", "The sale id must be a non-empty identifier or absent."));
        }

        if (command.Lines is not { Count: > 0 } lines)
        {
            return Fail(SalesErrors.Invalid("sale.no_lines", "lines", "A sale must have at least one line."));
        }

        if (lines.Count > SalesErrors.MaxLines)
        {
            return Fail(SalesErrors.Invalid("sale.too_many_lines", "lines", $"A sale can have at most {SalesErrors.MaxLines} lines."));
        }

        var requested = new List<(Guid ProductId, Quantity Quantity)>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index] is not { ProductId: { } productId } line || productId == Guid.Empty)
            {
                return Fail(SalesErrors.Invalid("sale.line_product_required", Field(index, "productId"), "A line must be for a product."));
            }

            if (line.Quantity is null)
            {
                return Fail(SalesErrors.Invalid("sale.line_quantity_required", Field(index, "quantity"), "The quantity sold is required."));
            }

            var quantity = SalesErrors.Run(() => Quantity.Create(line.Quantity.Value), _ => Field(index, "quantity"));
            if (quantity.IsFailure)
            {
                return Fail(quantity.Error!);
            }

            if (quantity.Value.Value == 0m)
            {
                return Fail(SalesErrors.Invalid("sale.line_quantity_required", Field(index, "quantity"), "The quantity sold must be greater than zero."));
            }

            requested.Add((productId, quantity.Value));
        }

        var currency = await OrganizationCurrency.ResolveAsync(db, user.OrganizationId, cancellationToken);
        if (currency.IsFailure)
        {
            return Fail(currency.Error!);
        }

        // A sale without an id is a new sale each time; with one, a retry is recognized.
        var saleId = command.SaleId ?? ids.NewId();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await recorder.ExecuteWithRetryAsync(() => SellOnceAsync(saleId, requested, currency.Value, cancellationToken), cancellationToken);
            }
            catch (SaleRaceException) when (attempt < StockMovementRecorder.MaxAttempts)
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(5, 40), cancellationToken);
            }
            catch (SaleRaceException)
            {
                db.ClearChangeTracker();
                return Fail(SalesErrors.ConcurrencyConflict);
            }
        }
    }

    private async Task<Result<SaleResultDto>> SellOnceAsync(Guid saleId, List<(Guid ProductId, Quantity Quantity)> requested, Currency currency, CancellationToken cancellationToken)
    {
        var includeCost = user.Has(Permission.ProductViewCost);

        // Has this sale id been used already (the stored sale is filtered to the caller's organization)? The same sale
        // again is a repeat and changes nothing; anything different under the same id is a conflict.
        var recorded = await db.Set<Sale>().AsNoTracking().Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == saleId, cancellationToken);
        if (recorded is not null)
        {
            return recorded.HasSameLinesAs(requested)
                ? Result.Success(new SaleResultDto(saleId, AlreadyRecorded: true, SaleDto.From(recorded, includeCost)))
                : Fail(SalesErrors.SaleIdConflict);
        }

        // Filtered to the caller's organization: another organization's product is not found.
        var productIds = requested.Select(line => line.ProductId).Distinct().ToArray();
        var products = await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var inputs = new List<SaleLineInput>(requested.Count);
        for (var index = 0; index < requested.Count; index++)
        {
            var (productId, quantity) = requested[index];
            if (!products.TryGetValue(productId, out var product))
            {
                return Fail(SalesErrors.ProductNotFound(productId, Field(index, "productId")));
            }

            if (!product.IsActive)
            {
                return Fail(SalesErrors.ProductInactive(Field(index, "productId")));
            }

            // The catalog price is the price charged; the cost price is recorded for margin. Both are snapshots.
            inputs.Add(new SaleLineInput(ids.NewId(), product.Id, product.Sku, product.Name, product.UnitOfMeasure, quantity, product.SellingPrice, product.SellingPrice, product.CostPrice, PriceOverrideReason: null));
        }

        var organizationId = user.OrganizationId!.Value;
        var now = clock.UtcNow;
        var created = SalesErrors.Run(() => Sale.Complete(saleId, organizationId, currency, user.UserId!.Value, now, inputs), SaleField);
        if (created.IsFailure)
        {
            return Fail(created.Error!);
        }

        var sale = created.Value;

        // Stage one movement per line. A refused movement (not enough stock) leaves nothing saved: this attempt simply
        // returns the failure, and nothing staged is ever stored.
        var opened = new List<Guid>();
        foreach (var line in sale.Lines)
        {
            var staged = await recorder.StageAsync(
                new RecordMovementCommand(line.ProductId, -line.Quantity.Value, InventoryMovementReason.SaleCompleted, InventorySourceType.Sale, sale.Id, user.UserId!.Value, Note: null),
                cancellationToken);
            if (staged.IsFailure)
            {
                return Fail(staged.Error!);
            }

            if (staged.Value.AlreadyRecorded)
            {
                // Another request recorded this sale after the check above: start over, which will find its sale.
                throw new SaleRaceException();
            }

            if (staged.Value.OpenedLevel)
            {
                opened.Add(line.ProductId);
            }
        }

        db.Set<Sale>().Add(sale);
        audit.Record(new AuditEntry(
            organizationId,
            AuditActions.SaleCreated,
            user.UserId,
            "Sale",
            sale.Id,
            string.Create(CultureInfo.InvariantCulture, $"A sale with {sale.Lines.Count} line(s) was recorded.")));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException)
        {
            // Recognize what the database now holds instead of assuming: a concurrent request may have recorded this
            // sale id (the sale key or the movement idempotency index refused ours) or opened a product's first stock
            // level. Starting over finds the stored sale: the same one is a repeat, a different one a conflict.
            if (await db.Set<Sale>().AnyAsync(s => s.Id == saleId, cancellationToken))
            {
                throw new SaleRaceException();
            }

            foreach (var productId in opened)
            {
                if (await db.Set<StockLevel>().AnyAsync(level => level.ProductId == productId, cancellationToken))
                {
                    throw new StockLevelRaceException(exception);
                }
            }

            throw;
        }

        return Result.Success(new SaleResultDto(saleId, AlreadyRecorded: false, SaleDto.From(sale, includeCost)));
    }

    private static string Field(int index, string name) => $"lines[{index}].{name}";

    private static string? SaleField(string code) => code switch
    {
        "sale.no_lines" or "sale.line_product_duplicate" => "lines",
        _ => null,
    };

    private static Result<SaleResultDto> Fail(Error error) => Result.Failure<SaleResultDto>(error);

    /// <summary>Thrown inside the handler when a concurrent request recorded the same sale, so the attempt starts over and finds it.</summary>
    private sealed class SaleRaceException : Exception;
}
