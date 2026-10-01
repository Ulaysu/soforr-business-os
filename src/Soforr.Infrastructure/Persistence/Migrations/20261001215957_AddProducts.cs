using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SellingPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    SellingPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CostPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    CostPriceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ReorderPoint = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ReorderLeadTimeDays = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SkuKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, computedColumnSql: "lower(\"Sku\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_CostPrice_NotNegative", "\"CostPrice\" >= 0");
                    table.CheckConstraint("CK_Products_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Products_PriceCurrency_Format", "\"SellingPriceCurrency\" ~ '^[A-Z]{3}$' AND \"CostPriceCurrency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_Products_PriceCurrency_Same", "\"SellingPriceCurrency\" = \"CostPriceCurrency\"");
                    table.CheckConstraint("CK_Products_ReorderLeadTimeDays_NotNegative", "\"ReorderLeadTimeDays\" IS NULL OR \"ReorderLeadTimeDays\" >= 0");
                    table.CheckConstraint("CK_Products_ReorderPoint_NotNegative", "\"ReorderPoint\" IS NULL OR \"ReorderPoint\" >= 0");
                    table.CheckConstraint("CK_Products_SellingPrice_NotNegative", "\"SellingPrice\" >= 0");
                    table.CheckConstraint("CK_Products_Sku_NotBlank", "char_length(btrim(\"Sku\")) > 0");
                    table.CheckConstraint("CK_Products_UnitOfMeasure_NotBlank", "char_length(btrim(\"UnitOfMeasure\")) > 0");
                    table.ForeignKey(
                        name: "FK_Products_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_OrganizationId_SkuKey",
                table: "Products",
                columns: new[] { "OrganizationId", "SkuKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
