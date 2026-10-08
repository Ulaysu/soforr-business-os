using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OfflineGrantId",
                table: "Sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAtUtc",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // A sale recorded before offline support was received when it happened.
            migrationBuilder.Sql("UPDATE \"Sales\" SET \"ReceivedAtUtc\" = \"CreatedAtUtc\"");

            migrationBuilder.CreateTable(
                name: "OfflineGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineGrants", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_OfflineGrants_Expiry_AfterIssue", "\"AbsoluteExpiresAtUtc\" > \"IssuedAtUtc\"");
                    table.CheckConstraint("CK_OfflineGrants_Revoked_NotBeforeIssue", "\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"IssuedAtUtc\"");
                    table.CheckConstraint("CK_OfflineGrants_Superseded_NotBeforeIssue", "\"SupersededAtUtc\" IS NULL OR \"SupersededAtUtc\" >= \"IssuedAtUtc\"");
                });

            migrationBuilder.CreateTable(
                name: "OfflineContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineContacts", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_OfflineContacts_OfflineGrants_OrganizationId_GrantId",
                        columns: x => new { x.OrganizationId, x.GrantId },
                        principalTable: "OfflineGrants",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineContacts_OrganizationId_GrantId_ObservedAtUtc",
                table: "OfflineContacts",
                columns: new[] { "OrganizationId", "GrantId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineGrants_OneActivePerUser",
                table: "OfflineGrants",
                columns: new[] { "OrganizationId", "UserId" },
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL AND \"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OfflineGrants_OrganizationId_IssuedAtUtc",
                table: "OfflineGrants",
                columns: new[] { "OrganizationId", "IssuedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfflineContacts");

            migrationBuilder.DropTable(
                name: "OfflineGrants");

            migrationBuilder.DropColumn(
                name: "OfflineGrantId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ReceivedAtUtc",
                table: "Sales");
        }
    }
}
