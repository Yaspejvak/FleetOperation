using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fserp.FleetOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFleetVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "fleet");

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorKind = table.Column<int>(type: "integer", nullable: false),
                    ActorSubjectId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActorClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActorUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Module = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    FailureDomain = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OperationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Changes = table.Column<string>(type: "jsonb", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                schema: "fleet",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plate_number = table.Column<string>(type: "text", nullable: false),
                    vehicle_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    capacity_kg = table.Column<decimal>(type: "numeric", nullable: false),
                    operational_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    maintenance_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    committed_mission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_entries_ActorSubjectId",
                schema: "audit",
                table: "entries",
                column: "ActorSubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_entries_CorrelationId",
                schema: "audit",
                table: "entries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_entries_EntityType_EntityId",
                schema: "audit",
                table: "entries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_entries_OccurredAtUtc",
                schema: "audit",
                table: "entries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "ux_vehicles_plate_number",
                schema: "fleet",
                table: "vehicles",
                column: "plate_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "vehicles",
                schema: "fleet");
        }
    }
}
