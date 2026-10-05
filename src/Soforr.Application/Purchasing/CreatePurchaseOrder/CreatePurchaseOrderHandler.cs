using Microsoft.EntityFrameworkCore;
using Soforr.Application.Audit;
using Soforr.Application.Common;
using Soforr.Application.Identity;
using Soforr.Application.Organizations;
using Soforr.Application.Persistence;
using Soforr.Domain.Catalog;
using Soforr.Domain.Common;
using Soforr.Domain.Purchasing;

namespace Soforr.Application.Purchasing.CreatePurchaseOrder;

/// <summary>One line to order. The unit cost is an amount in the organization's currency; the request never names a currency.</summary>
public sealed record CreatePurchaseOrderLine(Guid? ProductId, decimal? Quantity, decimal? UnitCost);

/// <summary>Creates a draft order for a supplier with its lines. The order's currency is the organization's, never the caller's choice.</summary>
public sealed record CreatePurchaseOrderCommand(Guid? SupplierId, string? Reference, IReadOnlyList<CreatePurchaseOrderLine>? Lines);

public sealed class CreatePurchaseOrderHandler(IAppDbContext db, IClock clock, IIdGenerator ids, ICurrentUser user, IAuditLog audit)
    : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> HandleAsync(CreatePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (user.Require(Permission.PurchasingManage) is { } denied)
        {
            return Result.Failure<PurchaseOrderDto>(denied);
        }

        if (command.SupplierId is not { } supplierId || supplierId == Guid.Empty)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.supplier_required", "supplierId", "The supplier is required."));
        }

        if (command.Lines is not { Count: > 0 } lines)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.no_lines", "lines", "An order needs at least one line."));
        }

        if (lines.Count > PurchasingErrors.MaxLines)
        {
            return Fail(PurchasingErrors.Invalid("purchase_order.too_many_lines", "lines", $"An order can have at most {PurchasingErrors.MaxLines} lines."));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (Validate(lines[index], index) is { } invalid)
            {
                return Fail(invalid);
            }
        }

        var currency = await OrganizationCurrency.ResolveAsync(db, user.OrganizationId, cancellationToken);
        if (currency.IsFailure)
        {
            return Fail(currency.Error!);
        }

        // Both lookups are filtered to the caller's organization: another organization's supplier or product is not found.
        var supplier = await db.Set<Supplier>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == supplierId, cancellationToken);
        if (supplier is null)
        {
            return Fail(PurchasingErrors.SupplierNotFound(supplierId));
        }

        if (!supplier.IsActive)
        {
            return Fail(PurchasingErrors.SupplierInactive);
        }

        var productIds = lines.Select(line => line.ProductId!.Value).Distinct().ToArray();
        var products = await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        for (var index = 0; index < lines.Count; index++)
        {
            var productId = lines[index].ProductId!.Value;
            if (!products.TryGetValue(productId, out var product))
            {
                return Fail(PurchasingErrors.ProductNotFound(productId, Field(index, "productId")));
            }

            if (!product.IsActive)
            {
                return Fail(PurchasingErrors.ProductInactive(Field(index, "productId")));
            }
        }

        var organizationId = user.OrganizationId!.Value;
        var now = clock.UtcNow;
        var created = PurchasingErrors.Run(() => PurchaseOrder.CreateDraft(ids.NewId(), organizationId, supplierId, currency.Value, command.Reference, now), OrderField);
        if (created.IsFailure)
        {
            return Fail(created.Error!);
        }

        var order = created.Value;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var product = products[line.ProductId!.Value];
            var added = PurchasingErrors.Run(
                () => order.AddLine(
                    ids.NewId(),
                    product.Id,
                    product.Sku,
                    product.Name,
                    product.UnitOfMeasure,
                    Quantity.Create(line.Quantity!.Value),
                    Money.Create(line.UnitCost!.Value, currency.Value),
                    now),
                code => LineField(code, index));
            if (added.IsFailure)
            {
                return Fail(added.Error!);
            }
        }

        db.Set<PurchaseOrder>().Add(order);
        audit.Record(new AuditEntry(organizationId, AuditActions.PurchaseOrderCreated, user.UserId, "PurchaseOrder", order.Id, $"A purchase order with {order.Lines.Count} line(s) was created."));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success(PurchaseOrderDto.From(order));
    }

    private static Result<PurchaseOrderDto> Fail(Error error) => Result.Failure<PurchaseOrderDto>(error);

    private static string Field(int index, string name) => $"lines[{index}].{name}";

    private static Error? Validate(CreatePurchaseOrderLine? line, int index)
    {
        if (line is null || line.ProductId is not { } productId || productId == Guid.Empty)
        {
            return PurchasingErrors.Invalid("purchase_order.line_product_required", Field(index, "productId"), "A line must be for a product.");
        }

        if (line.Quantity is null)
        {
            return PurchasingErrors.Invalid("purchase_order.line_quantity_required", Field(index, "quantity"), "The ordered quantity is required.");
        }

        if (line.UnitCost is null)
        {
            return PurchasingErrors.Invalid("purchase_order.line_unit_cost_required", Field(index, "unitCost"), "The unit cost is required.");
        }

        return null;
    }

    private static string? OrderField(string code) => code switch
    {
        "purchase_order.supplier_required" => "supplierId",
        "purchase_order.reference_too_long" => "reference",
        _ => null,
    };

    private static string? LineField(string code, int index) => code switch
    {
        "purchase_order.line_product_duplicate" => Field(index, "productId"),
        "purchase_order.line_quantity_required" or "quantity.negative" or "quantity.scale_exceeded" or "quantity.too_large" => Field(index, "quantity"),
        "purchase_order.line_unit_cost_negative" or "money.amount_out_of_range" or "money.scale_exceeded" => Field(index, "unitCost"),
        _ => null,
    };
}
