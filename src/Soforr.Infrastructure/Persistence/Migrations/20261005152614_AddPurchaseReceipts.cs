using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReceipts", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_PurchaseOrders_PurchaseOrderId_Organizatio~",
                        columns: x => new { x.PurchaseOrderId, x.OrganizationId },
                        principalTable: "PurchaseOrders",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReceiptLines",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReceiptLines", x => new { x.OrganizationId, x.ReceiptId, x.LineId });
                    table.CheckConstraint("CK_PurchaseReceiptLines_Quantity_Positive", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReceiptLines_Products_ProductId_OrganizationId",
                        columns: x => new { x.ProductId, x.OrganizationId },
                        principalTable: "Products",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceiptLines_PurchaseReceipts_OrganizationId_Receip~",
                        columns: x => new { x.OrganizationId, x.ReceiptId },
                        principalTable: "PurchaseReceipts",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ProductId_OrganizationId",
                table: "PurchaseReceiptLines",
                columns: new[] { "ProductId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_PurchaseOrderId_OrganizationId",
                table: "PurchaseReceipts",
                columns: new[] { "PurchaseOrderId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseReceiptLines");

            migrationBuilder.DropTable(
                name: "PurchaseReceipts");
        }
    }
}
