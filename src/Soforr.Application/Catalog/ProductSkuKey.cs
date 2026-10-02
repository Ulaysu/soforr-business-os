namespace Soforr.Application.Catalog;

/// <summary>
/// The database's case-insensitive comparison key of a product's SKU: a stored generated column <c>lower("Sku")</c>
/// behind the unique index that enforces ADR 0006. Lookups compare this column with the lowercased input, so the
/// lookup and the uniqueness rule cannot differ. Infrastructure maps the column under this name.
/// </summary>
public static class ProductSkuKey
{
    public const string PropertyName = "SkuKey";

    /// <summary>The key of a SKU as the database computes it (for the characters the database lowercases the same way).</summary>
    public static string Of(string sku)
    {
        ArgumentNullException.ThrowIfNull(sku);

        return sku.Trim().ToLowerInvariant();
    }
}
