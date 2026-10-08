using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Inventory;
using Soforr.Application.Offline;
using Soforr.Application.Organizations;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Customers;
using Soforr.Domain.Inventory;
using Soforr.Domain.Sales;

namespace Soforr.Application.Sales.CreateSale;

/// <summary>
/// One product to sell and how much of it. Without a <see cref="UnitPrice"/> the line is charged the product's catalog price at that
/// moment. A <see cref="UnitPrice"/> is the final price charged for one unit (an override or a discount, never negative); stating one
/// needs the <c>SalesOverridePrice</c> permission, and a price that differs from the catalog price needs a <see cref="PriceReason"/>.
/// A price below the product's cost price additionally needs <c>SalesSellBelowCost</c>.
/// <para>
/// <see cref="CatalogPriceShown"/> is the catalog price the cashier saw when the sale was made; it is required for a sale recorded
/// offline and, when given, must equal the product's current catalog price, otherwise the sale is refused as a conflict
/// (<c>sale.catalog_price_changed</c>). It is compared with the catalog price only: a final price charged is never taken as evidence
/// of a changed catalog price, so a legitimate override or negotiated price is not mistaken for drift.
/// </para>
/// </summary>
public sealed record CreateSaleLine(Guid? ProductId, decimal? Quantity, decimal? UnitPrice = null, string? PriceReason = null, decimal? CatalogPriceShown = null);

/// <summary>
/// Records a sale of products. <see cref="SaleId"/> is an optional, client-chosen identifier of this sale: sending the
/// same one again (a retry after a timeout, a double click) changes nothing and reports that it was already recorded,
/// provided it asks for exactly the same products, quantities, customer and amount paid; the same id with anything
/// different is a conflict. Without an id a new sale is recorded each time.
/// <para>
/// <see cref="CustomerId"/> makes it a sale on account: <see cref="AmountPaid"/> is then what the customer pays now, from
/// nothing (all on credit) to the whole total, and the rest is owed from a due date fixed now (the sale date plus the
/// customer's credit terms, or immediately when the customer has none). An absent <see cref="AmountPaid"/> means paid in full, so a
/// debt is never created by leaving something out. Without a customer the sale is a walk-in sale, always paid in full: a stated
/// amount must equal the total. <see cref="PaymentMethod"/> is a free-text label recorded with the payment taken now (a walk-in
/// sale records no payment, so it has no label).
/// </para>
/// <para>
/// A sale recorded offline names the time it happened (<see cref="OccurredAtUtc"/>) and the <see cref="OfflineGrantId"/> that vouches for
/// it (ADR 0008). The server accepts that time only inside a silent gap it observed on that grant, within the offline window; otherwise
/// the sale is refused (nothing recorded, no id reserved) and is never silently dated at server time. The server records its own receipt
/// time separately. Without these the sale happens at server time, as always.
/// </para>
/// </summary>
public sealed record CreateSaleCommand(
    Guid? SaleId,
    IReadOnlyList<CreateSaleLine>? Lines,
    Guid? CustomerId = null,
    decimal? AmountPaid = null,
    string? PaymentMethod = null,
    DateTimeOffset? OccurredAtUtc = null,
    Guid? OfflineGrantId = null);

/// <summary>
/// The selling use case (ADR 0004). In one transaction it records the sale (with the prices and costs of its products as
/// snapshots), writes one <c>SaleCompleted</c> movement per line through the stock recorder, which removes the stock and
/// refuses a line that would take more than exists, and records the audit event; they are saved together or not at all.
/// The sale id is the sale's key and the movements' source id, so a repeat is recognized by the stored sale (and refused
/// by the unique keys if two arrive together). A conflict with another writer of the same stock is retried by the
/// recorder; each attempt starts from a cleared change tracker and re-validates, so stock that ran out meanwhile is
/// reported as the business error it is.
/// </summary>
public sealed class CreateSaleHandler(IAppDbContext db, StockMovementRecorder recorder, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit, OfflineEventTimeValidator offline)
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

        if (command.CustomerId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("sale.customer_invalid", "customerId", "The customer must be a non-empty identifier or absent."));
        }

        if (command.OfflineGrantId == Guid.Empty)
        {
            return Fail(SalesErrors.Invalid("offline.grant_invalid", "offlineGrantId", "The offline grant must be a non-empty identifier or absent."));
        }

        if (command.OfflineGrantId is not null && command.OccurredAtUtc is null)
        {
            return Fail(OfflineErrors.EventTimeRequired);
        }

        if (command.OccurredAtUtc is { } claimed)
        {
            if (command.OfflineGrantId is null)
            {
                return Fail(OfflineErrors.GrantRequired);
            }

            if (claimed.Offset != TimeSpan.Zero)
            {
                return Fail(SalesErrors.Invalid("sale.timestamp_not_utc", "occurredAtUtc", "Timestamps must be in UTC."));
            }

            // The database keeps microseconds. The time is truncated the same way before it is judged, stored and compared on a repeat.
            command = command with { OccurredAtUtc = new DateTimeOffset(claimed.Ticks - (claimed.Ticks % 10), claimed.Offset) };
        }

        if (command.Lines is not { Count: > 0 } lines)
        {
            return Fail(SalesErrors.Invalid("sale.no_lines", "lines", "A sale must have at least one line."));
        }

        if (lines.Count > SalesErrors.MaxLines)
        {
            return Fail(SalesErrors.Invalid("sale.too_many_lines", "lines", $"A sale can have at most {SalesErrors.MaxLines} lines."));
        }

        var requested = new List<RequestedLine>(lines.Count);
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

            if (line.UnitPrice is null && line.PriceReason is not null)
            {
                return Fail(SalesErrors.Invalid("sale.line_price_reason_without_price", Field(index, "priceReason"), "A price reason can be given only together with a unit price."));
            }

            if (line.UnitPrice is { } stated)
            {
                // Stating a price at all is a controlled action, checked before anything is looked up or recorded.
                if (user.Require(Permission.SalesOverridePrice) is { } notAllowed)
                {
                    return Fail(notAllowed);
                }

                if (stated < 0m)
                {
                    return Fail(SalesErrors.Invalid("sale.line_price_negative", Field(index, "unitPrice"), "The unit price cannot be negative."));
                }
            }

            if (command.OccurredAtUtc is not null && line.CatalogPriceShown is null)
            {
                return Fail(SalesErrors.Invalid("sale.catalog_price_required", Field(index, "catalogPriceShown"), "A sale recorded offline must state the catalog price the cashier saw on every line."));
            }

            requested.Add(new RequestedLine(productId, quantity.Value, line.UnitPrice, line.PriceReason, line.CatalogPriceShown));
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
                return await recorder.ExecuteWithRetryAsync(() => SellOnceAsync(saleId, requested, command, currency.Value, cancellationToken), cancellationToken);
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

    private async Task<Result<SaleResultDto>> SellOnceAsync(Guid saleId, List<RequestedLine> requested, CreateSaleCommand command, Currency currency, CancellationToken cancellationToken)
    {
        var includeCost = user.Has(Permission.ProductViewCost);

        // Has this sale id been used already (the stored sale is filtered to the caller's organization)? The same sale
        // again is a repeat and changes nothing; anything different under the same id is a conflict.
        var recorded = await db.Set<Sale>().AsNoTracking().Include(s => s.Lines).Include(s => s.Payments).SingleOrDefaultAsync(s => s.Id == saleId, cancellationToken);
        if (recorded is not null)
        {
            var asked = new List<(Guid ProductId, Quantity Quantity, Money? UnitPrice, string? Reason)>(requested.Count);
            foreach (var line in requested)
            {
                var price = line.UnitPrice is { } amount ? SalesErrors.Run(() => Money.Create(amount, currency), _ => null) : null;
                if (price is { IsFailure: true })
                {
                    return Fail(price.Error!);
                }

                asked.Add((line.ProductId, line.Quantity, price?.Value, line.PriceReason));
            }

            var sameTime = command.OccurredAtUtc is null
                ? recorded.OfflineGrantId is null
                : recorded.OfflineGrantId == command.OfflineGrantId && recorded.CreatedAtUtc == command.OccurredAtUtc;

            return sameTime && recorded.HasSameRequestedLinesAs(asked) && recorded.HasSameSettlementAs(command.CustomerId, command.AmountPaid)
                ? Result.Success(new SaleResultDto(saleId, AlreadyRecorded: true, SaleDto.From(recorded, includeCost)))
                : Fail(SalesErrors.SaleIdConflict);
        }

        // An offline sale is judged before anything is looked up or staged: the server must have vouched for its time (ADR 0008).
        // A refusal records nothing (but its audit event) and reserves no id.
        AcceptedOfflineEvent? accepted = null;
        if (command.OccurredAtUtc is { } eventTime)
        {
            var vouched = await offline.ValidateAsync(command.OfflineGrantId, eventTime, "sale", cancellationToken);
            if (vouched.IsFailure)
            {
                return Fail(vouched.Error!);
            }

            accepted = vouched.Value;
        }

        // Filtered to the caller's organization: another organization's product is not found.
        var productIds = requested.Select(line => line.ProductId).Distinct().ToArray();
        var products = await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var inputs = new List<SaleLineInput>(requested.Count);
        for (var index = 0; index < requested.Count; index++)
        {
            var (productId, quantity, requestedPrice, reason, shownCatalogPrice) = requested[index];
            if (!products.TryGetValue(productId, out var product))
            {
                return Fail(SalesErrors.ProductNotFound(productId, Field(index, "productId")));
            }

            if (!product.IsActive)
            {
                return Fail(SalesErrors.ProductInactive(Field(index, "productId")));
            }

            // The catalog price is the default price; a stated price is the final price charged. The catalog price, the price charged
            // and the cost price are all recorded as snapshots, so margin and audit stay explainable.
            var charged = product.SellingPrice;
            if (requestedPrice is { } statedPrice)
            {
                var money = SalesErrors.Run(() => Money.Create(statedPrice, currency), _ => Field(index, "unitPrice"));
                if (money.IsFailure)
                {
                    return Fail(money.Error!);
                }

                charged = money.Value;
                if (charged.Amount < product.CostPrice.Amount && !user.Has(Permission.SalesSellBelowCost))
                {
                    return Fail(new Error("sale.line_price_below_cost", ErrorKind.Forbidden, "The price is below the product's cost price. Only the Owner may sell below cost.", Field(index, "unitPrice")));
                }
            }

            // The catalog price the cashier saw must still be the catalog price. Only the catalog price decides this: the final price
            // charged (an authorized override or a negotiated price) is never evidence of drift.
            if (shownCatalogPrice is { } shown && shown != product.SellingPrice.Amount)
            {
                return Fail(new Error("sale.catalog_price_changed", ErrorKind.Conflict, "The catalog price changed since the cashier saw it. Nothing was recorded; review the price and submit again.", Field(index, "catalogPriceShown")));
            }

            inputs.Add(new SaleLineInput(ids.NewId(), product.Id, product.Sku, product.Name, product.UnitOfMeasure, quantity, product.SellingPrice, charged, product.CostPrice, reason));
        }

        var organizationId = user.OrganizationId!.Value;

        // When the sale happened: the vouched event time for an offline sale, the server's clock otherwise. The server's receipt time is separate.
        var receivedAt = accepted?.ReceivedAtUtc ?? clock.UtcNow;
        var now = accepted?.EventTimeUtc ?? receivedAt;

        // How the sale is settled: a walk-in sale is paid in full and records no payment; a sale on account takes what the
        // customer pays now (the whole total when nothing is stated) and is due after the customer's terms, fixed from today.
        DateTimeOffset? dueAtUtc = null;
        Money? initialPayment = null;
        if (command.CustomerId is { } customerId)
        {
            // Filtered to the caller's organization: another organization's customer is not found.
            var customer = await db.Set<Customer>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
            if (customer is null)
            {
                return Fail(SalesErrors.CustomerNotFound(customerId, "customerId"));
            }

            if (!customer.IsActive)
            {
                return Fail(SalesErrors.CustomerInactive);
            }

            dueAtUtc = now.AddDays(customer.CreditTermsDays ?? 0);
            var paid = SalesErrors.Run(
                () => command.AmountPaid is { } amount ? Money.Create(amount, currency) : Sale.TotalOf(currency, inputs),
                _ => "amountPaid");
            if (paid.IsFailure)
            {
                return Fail(paid.Error!);
            }

            initialPayment = paid.Value;
        }
        else if (command.AmountPaid is { } stated)
        {
            var total = SalesErrors.Run(() => Sale.TotalOf(currency, inputs));
            if (total.IsFailure)
            {
                return Fail(total.Error!);
            }

            if (stated != total.Value.Amount)
            {
                return Fail(SalesErrors.Invalid("sale.walk_in_is_paid_in_full", "amountPaid", "A sale without a customer is paid in full: the amount paid must equal the total, or be left out."));
            }
        }

        var created = SalesErrors.Run(() => Sale.Complete(saleId, organizationId, currency, user.UserId!.Value, now, inputs, command.CustomerId, dueAtUtc, initialPayment, command.PaymentMethod, receivedAt, accepted?.GrantId), SaleField);
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
            string.Create(CultureInfo.InvariantCulture, $"A sale with {sale.Lines.Count} line(s) was recorded{(sale.CustomerId is null ? string.Empty : " on account")}.")));

        if (accepted is not null)
        {
            offline.RecordAccepted(accepted, AuditActions.OfflineSaleAccepted, sale.Id, "Sale", "sale");
        }

        if (sale.HasPriceOverride)
        {
            var overridden = sale.Lines.Count(line => line.IsPriceOverridden);
            var belowCost = sale.Lines.Count(line => line.UnitPrice.Amount < line.CostPrice.Amount);
            audit.Record(new AuditEntry(
                organizationId,
                AuditActions.SalePriceOverridden,
                user.UserId,
                "Sale",
                sale.Id,
                string.Create(CultureInfo.InvariantCulture, $"{overridden} of {sale.Lines.Count} line(s) were charged a price different from the catalog price (catalog total minus charged total: {sale.TotalDiscount.Amount}); {belowCost} below cost.")));
        }

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

    /// <summary>A requested line: the product, the quantity and, when the caller states one, the final unit price and its reason.</summary>
    private sealed record RequestedLine(Guid ProductId, Quantity Quantity, decimal? UnitPrice, string? PriceReason, decimal? CatalogPriceShown);

    private static string Field(int index, string name) => $"lines[{index}].{name}";

    private static string? SaleField(string code) => code switch
    {
        "sale.no_lines" or "sale.line_product_duplicate" => "lines",
        "sale.customer_invalid" => "customerId",
        "sale.payment_exceeds_total" or "sale.payment_amount_negative" or "sale.payment_currency_mismatch" or "sale.walk_in_is_paid_in_full" or "money.scale_exceeded" or "money.amount_out_of_range" => "amountPaid",
        "sale.payment_method_too_long" => "paymentMethod",
        _ => null,
    };

    private static Result<SaleResultDto> Fail(Error error) => Result.Failure<SaleResultDto>(error);

    /// <summary>Thrown inside the handler when a concurrent request recorded the same sale, so the attempt starts over and finds it.</summary>
    private sealed class SaleRaceException : Exception;
}
