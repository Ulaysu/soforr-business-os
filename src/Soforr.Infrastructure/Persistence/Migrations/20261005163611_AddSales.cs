using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VoidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                    // "xmin" (the concurrency token, ADR 0004) is a PostgreSQL system column on every table: it is mapped in the
                    // model but must not be created here.
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sales", x => x.Id);
                    table.UniqueConstraint("AK_Sales_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_Sales_Currency_Format", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_Sales_Status_Known", "\"Status\" IN ('Completed', 'Voided')");
                    table.CheckConstraint("CK_Sales_Void_MatchesStatus", "(\"Status\" = 'Voided') = (\"VoidedAtUtc\" IS NOT NULL) AND (\"Status\" = 'Voided') = (\"VoidedByUserId\" IS NOT NULL) AND (\"Status\" = 'Voided') = (\"VoidReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_Sales_VoidedAt_NotBeforeSale", "\"VoidedAtUtc\" IS NULL OR \"VoidedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_Sales_VoidReason_NotBlank", "\"VoidReason\" IS NULL OR char_length(btrim(\"VoidReason\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "SaleLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkuSnapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UnitOfMeasureSnapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CatalogUnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    CatalogUnitPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CostPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    CostPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceOverrideReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleLines", x => x.Id);
                    table.CheckConstraint("CK_SaleLines_CatalogUnitPrice_NotNegative", "\"CatalogUnitPrice\" >= 0");
                    table.CheckConstraint("CK_SaleLines_CostPrice_NotNegative", "\"CostPrice\" >= 0");
                    table.CheckConstraint("CK_SaleLines_Currencies_Format", "\"UnitPriceCurrency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_SaleLines_Currencies_Match", "\"UnitPriceCurrency\" = \"CatalogUnitPriceCurrency\" AND \"UnitPriceCurrency\" = \"CostPriceCurrency\"");
                    table.CheckConstraint("CK_SaleLines_OverrideReason_MatchesPrice", "(\"UnitPrice\" <> \"CatalogUnitPrice\") = (\"PriceOverrideReason\" IS NOT NULL) AND (\"PriceOverrideReason\" IS NULL OR char_length(btrim(\"PriceOverrideReason\")) > 0)");
                    table.CheckConstraint("CK_SaleLines_Quantity_Positive", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_SaleLines_Snapshots_NotBlank", "char_length(btrim(\"SkuSnapshot\")) > 0 AND char_length(btrim(\"NameSnapshot\")) > 0 AND char_length(btrim(\"UnitOfMeasureSnapshot\")) > 0");
                    table.CheckConstraint("CK_SaleLines_UnitPrice_NotNegative", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_SaleLines_Products_ProductId_OrganizationId",
                        columns: x => new { x.ProductId, x.OrganizationId },
                        principalTable: "Products",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleLines_Sales_SaleId_OrganizationId",
                        columns: x => new { x.SaleId, x.OrganizationId },
                        principalTable: "Sales",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_ProductId_OrganizationId",
                table: "SaleLines",
                columns: new[] { "ProductId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_SaleId_OrganizationId",
                table: "SaleLines",
                columns: new[] { "SaleId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_SaleId_ProductId",
                table: "SaleLines",
                columns: new[] { "SaleId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_OrganizationId_CreatedAtUtc",
                table: "Sales",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_OrganizationId_Status_CreatedAtUtc",
                table: "Sales",
                columns: new[] { "OrganizationId", "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleLines");

            migrationBuilder.DropTable(
                name: "Sales");
        }
    }
}
