using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "Sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAtUtc",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentRevision",
                table: "Sales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SalePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MethodLabel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReversesPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalePayments", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("AK_SalePayments_OrganizationId_SaleId_Id", x => new { x.OrganizationId, x.SaleId, x.Id });
                    table.CheckConstraint("CK_SalePayments_Amount_Positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_SalePayments_AmountCurrency_Format", "\"AmountCurrency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_SalePayments_Kind_Known", "\"Kind\" IN ('Payment', 'Reversal')");
                    table.CheckConstraint("CK_SalePayments_MethodLabel_NotBlank", "\"MethodLabel\" IS NULL OR char_length(btrim(\"MethodLabel\")) > 0");
                    table.CheckConstraint("CK_SalePayments_PaidAt_NotAfterRecorded", "\"PaidAtUtc\" <= \"RecordedAtUtc\"");
                    table.CheckConstraint("CK_SalePayments_Reversal_MatchesKind", "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL) AND (\"Kind\" = 'Reversal') = (\"ReversalReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_SalePayments_ReversalReason_NotBlank", "\"ReversalReason\" IS NULL OR char_length(btrim(\"ReversalReason\")) > 0");
                    table.ForeignKey(
                        name: "FK_SalePayments_SalePayments_OrganizationId_SaleId_ReversesPay~",
                        columns: x => new { x.OrganizationId, x.SaleId, x.ReversesPaymentId },
                        principalTable: "SalePayments",
                        principalColumns: new[] { "OrganizationId", "SaleId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalePayments_Sales_OrganizationId_SaleId",
                        columns: x => new { x.OrganizationId, x.SaleId },
                        principalTable: "Sales",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_CustomerId_OrganizationId",
                table: "Sales",
                columns: new[] { "CustomerId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_OrganizationId_CustomerId_DueAtUtc",
                table: "Sales",
                columns: new[] { "OrganizationId", "CustomerId", "DueAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sales_Customer_MatchesDueDate",
                table: "Sales",
                sql: "(\"CustomerId\" IS NULL) = (\"DueAtUtc\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sales_DueAt_NotBeforeSale",
                table: "Sales",
                sql: "\"DueAtUtc\" IS NULL OR \"DueAtUtc\" >= \"CreatedAtUtc\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sales_PaymentRevision_NotNegative",
                table: "Sales",
                sql: "\"PaymentRevision\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_OrganizationId_ReversesPaymentId",
                table: "SalePayments",
                columns: new[] { "OrganizationId", "ReversesPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_OrganizationId_SaleId_RecordedAtUtc",
                table: "SalePayments",
                columns: new[] { "OrganizationId", "SaleId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_OrganizationId_SaleId_ReversesPaymentId",
                table: "SalePayments",
                columns: new[] { "OrganizationId", "SaleId", "ReversesPaymentId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Sales_Customers_CustomerId_OrganizationId",
                table: "Sales",
                columns: new[] { "CustomerId", "OrganizationId" },
                principalTable: "Customers",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sales_Customers_CustomerId_OrganizationId",
                table: "Sales");

            migrationBuilder.DropTable(
                name: "SalePayments");

            migrationBuilder.DropIndex(
                name: "IX_Sales_CustomerId_OrganizationId",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_OrganizationId_CustomerId_DueAtUtc",
                table: "Sales");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sales_Customer_MatchesDueDate",
                table: "Sales");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sales_DueAt_NotBeforeSale",
                table: "Sales");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sales_PaymentRevision_NotNegative",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "DueAtUtc",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PaymentRevision",
                table: "Sales");
        }
    }
}
