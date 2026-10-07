using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeSaleKeyToOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleLines_Sales_SaleId_OrganizationId",
                table: "SaleLines");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Sales_Id_OrganizationId",
                table: "Sales");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Sales",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_SaleLines_SaleId_OrganizationId",
                table: "SaleLines");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Sales",
                table: "Sales",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_OrganizationId_SaleId",
                table: "SaleLines",
                columns: new[] { "OrganizationId", "SaleId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SaleLines_Sales_OrganizationId_SaleId",
                table: "SaleLines",
                columns: new[] { "OrganizationId", "SaleId" },
                principalTable: "Sales",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleLines_Sales_OrganizationId_SaleId",
                table: "SaleLines");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Sales",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_SaleLines_OrganizationId_SaleId",
                table: "SaleLines");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Sales_Id_OrganizationId",
                table: "Sales",
                columns: new[] { "Id", "OrganizationId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Sales",
                table: "Sales",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_SaleLines_SaleId_OrganizationId",
                table: "SaleLines",
                columns: new[] { "SaleId", "OrganizationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_SaleLines_Sales_SaleId_OrganizationId",
                table: "SaleLines",
                columns: new[] { "SaleId", "OrganizationId" },
                principalTable: "Sales",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
