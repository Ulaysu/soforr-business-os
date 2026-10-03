using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContactDetails = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.UniqueConstraint("AK_Suppliers_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_Suppliers_ContactDetails_NotBlank", "\"ContactDetails\" IS NULL OR char_length(btrim(\"ContactDetails\")) > 0");
                    table.CheckConstraint("CK_Suppliers_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                    table.ForeignKey(
                        name: "FK_Suppliers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PlacedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                    // "xmin" (the concurrency token, ADR 0004) is a PostgreSQL system column on every table: it is mapped in the
                    // model but must not be created here.
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.UniqueConstraint("AK_PurchaseOrders_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_PurchaseOrders_ClosedAt_MatchesStatus", "(\"Status\" IN ('Received', 'Cancelled')) = (\"ClosedAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_PurchaseOrders_Currency_Format", "\"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_PurchaseOrders_PlacedAt_MatchesStatus", "(\"Status\" = 'Draft' AND \"PlacedAtUtc\" IS NULL) OR (\"Status\" IN ('Placed', 'PartiallyReceived', 'Received') AND \"PlacedAtUtc\" IS NOT NULL) OR \"Status\" = 'Cancelled'");
                    table.CheckConstraint("CK_PurchaseOrders_Reference_NotBlank", "\"Reference\" IS NULL OR char_length(btrim(\"Reference\")) > 0");
                    table.CheckConstraint("CK_PurchaseOrders_Status_Known", "\"Status\" IN ('Draft', 'Placed', 'PartiallyReceived', 'Received', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Suppliers_SupplierId_OrganizationId",
                        columns: x => new { x.SupplierId, x.OrganizationId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkuSnapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UnitOfMeasureSnapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    UnitCostCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderLines_OrderedQuantity_Positive", "\"OrderedQuantity\" > 0");
                    table.CheckConstraint("CK_PurchaseOrderLines_ReceivedQuantity_WithinOrdered", "\"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"OrderedQuantity\"");
                    table.CheckConstraint("CK_PurchaseOrderLines_Snapshots_NotBlank", "char_length(btrim(\"SkuSnapshot\")) > 0 AND char_length(btrim(\"NameSnapshot\")) > 0 AND char_length(btrim(\"UnitOfMeasureSnapshot\")) > 0");
                    table.CheckConstraint("CK_PurchaseOrderLines_UnitCost_NotNegative", "\"UnitCost\" >= 0");
                    table.CheckConstraint("CK_PurchaseOrderLines_UnitCostCurrency_Format", "\"UnitCostCurrency\" ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Products_ProductId_OrganizationId",
                        columns: x => new { x.ProductId, x.OrganizationId },
                        principalTable: "Products",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId_Organizat~",
                        columns: x => new { x.PurchaseOrderId, x.OrganizationId },
                        principalTable: "PurchaseOrders",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_ProductId_OrganizationId",
                table: "PurchaseOrderLines",
                columns: new[] { "ProductId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_PurchaseOrderId_OrganizationId",
                table: "PurchaseOrderLines",
                columns: new[] { "PurchaseOrderId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_PurchaseOrderId_ProductId",
                table: "PurchaseOrderLines",
                columns: new[] { "PurchaseOrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_OrganizationId_Status_CreatedAtUtc",
                table: "PurchaseOrders",
                columns: new[] { "OrganizationId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_OrganizationId_SupplierId",
                table: "PurchaseOrders",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_SupplierId_OrganizationId",
                table: "PurchaseOrders",
                columns: new[] { "SupplierId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_OrganizationId_Name",
                table: "Suppliers",
                columns: new[] { "OrganizationId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "Suppliers");
        }
    }
}
