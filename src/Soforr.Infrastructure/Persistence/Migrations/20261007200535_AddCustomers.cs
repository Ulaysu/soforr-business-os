using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContactDetails = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreditTermsDays = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.UniqueConstraint("AK_Customers_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.CheckConstraint("CK_Customers_ContactDetails_NotBlank", "\"ContactDetails\" IS NULL OR char_length(btrim(\"ContactDetails\")) > 0");
                    table.CheckConstraint("CK_Customers_CreditTermsDays_Range", "\"CreditTermsDays\" IS NULL OR (\"CreditTermsDays\" >= 0 AND \"CreditTermsDays\" <= 365)");
                    table.CheckConstraint("CK_Customers_Name_NotBlank", "char_length(btrim(\"Name\")) > 0");
                    table.ForeignKey(
                        name: "FK_Customers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_OrganizationId_IsActive_Name",
                table: "Customers",
                columns: new[] { "OrganizationId", "IsActive", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
