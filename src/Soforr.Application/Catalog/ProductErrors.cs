using Soforr.Application.Common;

namespace Soforr.Application.Catalog;

/// <summary>Errors and field names of the Catalog module.</summary>
internal static class ProductErrors
{
    public static Error NotFound(Guid id) =>
        new("product.not_found", ErrorKind.NotFound, $"The product '{id}' was not found.");

    public static Error SkuTaken { get; } =
        new("product.sku_taken", ErrorKind.Conflict, "A product with this SKU already exists. SKUs are compared without regard to case.", "sku");

    public static Error Required(string code, string field, string message) => new(code, ErrorKind.Validation, message, field);

    /// <summary>Maps a Domain error code raised while building a product to the request field it is about, or null.</summary>
    public static string? FieldFor(string code) => code switch
    {
        "product.sku_required" or "product.sku_too_long" => "sku",
        "product.name_required" or "product.name_too_long" => "name",
        "product.unit_of_measure_required" or "product.unit_of_measure_too_long" => "unitOfMeasure",
        "product.selling_price_negative" => "sellingPrice",
        "product.cost_price_negative" => "costPrice",
        "reorder_settings.lead_time_negative" => "reorderLeadTimeDays",
        _ => null,
    };
}
