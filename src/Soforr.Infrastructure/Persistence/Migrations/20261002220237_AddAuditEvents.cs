using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Soforr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AuditEvents_Action_Format", "\"Action\" ~ '^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$'");
                    table.CheckConstraint("CK_AuditEvents_Description_NotBlank", "\"Description\" IS NULL OR char_length(btrim(\"Description\")) > 0");
                    table.CheckConstraint("CK_AuditEvents_Entity_BothOrNeither", "(\"EntityType\" IS NULL) = (\"EntityId\" IS NULL)");
                    table.CheckConstraint("CK_AuditEvents_EntityType_NotBlank", "\"EntityType\" IS NULL OR char_length(btrim(\"EntityType\")) > 0");
                    table.ForeignKey(
                        name: "FK_AuditEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId_EntityType_EntityId",
                table: "AuditEvents",
                columns: new[] { "OrganizationId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId_OccurredAtUtc_Id",
                table: "AuditEvents",
                columns: new[] { "OrganizationId", "OccurredAtUtc", "Id" });

            // The audit trail is append-only: the database itself refuses to change or remove a recorded event,
            // whoever asks (the application, a script or a person with SQL access to the table).
            migrationBuilder.Sql(@"
CREATE FUNCTION ""AuditEvents_AppendOnly""() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'AuditEvents is append-only: % is not allowed.', TG_OP USING ERRCODE = 'restrict_violation';
END;
$$;");
            migrationBuilder.Sql(@"
CREATE TRIGGER ""TR_AuditEvents_AppendOnly"" BEFORE UPDATE OR DELETE ON ""AuditEvents""
    FOR EACH ROW EXECUTE FUNCTION ""AuditEvents_AppendOnly""();");
            migrationBuilder.Sql(@"
CREATE TRIGGER ""TR_AuditEvents_NoTruncate"" BEFORE TRUNCATE ON ""AuditEvents""
    FOR EACH STATEMENT EXECUTE FUNCTION ""AuditEvents_AppendOnly""();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table drops its triggers; the function is dropped after it.
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.Sql(@"DROP FUNCTION ""AuditEvents_AppendOnly""();");
        }
    }
}
