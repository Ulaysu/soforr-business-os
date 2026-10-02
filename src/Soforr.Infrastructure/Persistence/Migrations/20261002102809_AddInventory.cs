using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Products_Id_OrganizationId",
                table: "Products",
                columns: new[] { "Id", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityDelta = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.CheckConstraint("CK_InventoryMovements_Note_NotBlank", "\"Note\" IS NULL OR char_length(btrim(\"Note\")) > 0");
                    table.CheckConstraint("CK_InventoryMovements_Note_RequiredForAdjustment", "\"Reason\" <> 'ManualAdjustment' OR (\"Note\" IS NOT NULL AND char_length(btrim(\"Note\")) > 0)");
                    table.CheckConstraint("CK_InventoryMovements_QuantityDelta_NotZero", "\"QuantityDelta\" <> 0");
                    table.CheckConstraint("CK_InventoryMovements_Reason_Known", "\"Reason\" IN ('PurchaseReceived', 'SaleCompleted', 'ManualAdjustment', 'Return', 'Damage')");
                    table.CheckConstraint("CK_InventoryMovements_Reason_MatchesSign", "(\"Reason\" IN ('PurchaseReceived', 'Return') AND \"QuantityDelta\" > 0) OR (\"Reason\" IN ('SaleCompleted', 'Damage') AND \"QuantityDelta\" < 0) OR \"Reason\" = 'ManualAdjustment'");
                    table.CheckConstraint("CK_InventoryMovements_SourceType_Known", "\"SourceType\" IN ('PurchaseReceipt', 'Sale', 'Adjustment')");
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Products_ProductId_OrganizationId",
                        columns: x => new { x.ProductId, x.OrganizationId },
                        principalTable: "Products",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockLevels",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    // No "xmin" column: it is PostgreSQL's own system column on every table and is used as the
                    // optimistic concurrency token through the model (see StockLevelConfiguration). The Npgsql scaffold
                    // writes it here as an ordinary column; it is removed so no migration operation creates it.
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLevels", x => new { x.OrganizationId, x.ProductId });
                    table.CheckConstraint("CK_StockLevels_Quantity_NotNegative", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockLevels_Products_ProductId_OrganizationId",
                        columns: x => new { x.ProductId, x.OrganizationId },
                        principalTable: "Products",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_Idempotency",
                table: "InventoryMovements",
                columns: new[] { "OrganizationId", "SourceType", "SourceId", "ProductId", "Reason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_OrganizationId_ProductId_OccurredAtUtc",
                table: "InventoryMovements",
                columns: new[] { "OrganizationId", "ProductId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ProductId_OrganizationId",
                table: "InventoryMovements",
                columns: new[] { "ProductId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_ProductId_OrganizationId",
                table: "StockLevels",
                columns: new[] { "ProductId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "StockLevels");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Products_Id_OrganizationId",
                table: "Products");
        }
    }
}
