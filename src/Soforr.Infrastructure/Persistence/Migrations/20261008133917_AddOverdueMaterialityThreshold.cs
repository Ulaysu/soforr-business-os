using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOverdueMaterialityThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OverdueMaterialityThreshold",
                table: "Organizations",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Organizations_OverdueMaterialityThreshold_NotNegative",
                table: "Organizations",
                sql: "\"OverdueMaterialityThreshold\" IS NULL OR \"OverdueMaterialityThreshold\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Organizations_OverdueMaterialityThreshold_NotNegative",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "OverdueMaterialityThreshold",
                table: "Organizations");
        }
    }
}
