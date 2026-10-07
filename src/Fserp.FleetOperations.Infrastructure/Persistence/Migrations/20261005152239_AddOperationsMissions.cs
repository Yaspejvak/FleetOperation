using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fserp.FleetOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationsMissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "operations");

            migrationBuilder.CreateTable(
                name: "missions",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    required_capacity_kg = table.Column<decimal>(type: "numeric", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    assigned_vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_driver_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_missions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_missions_active_driver",
                schema: "operations",
                table: "missions",
                column: "assigned_driver_id",
                unique: true,
                filter: "status IN ('Assigned', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "ux_missions_active_vehicle",
                schema: "operations",
                table: "missions",
                column: "assigned_vehicle_id",
                unique: true,
                filter: "status IN ('Assigned', 'InProgress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "missions",
                schema: "operations");
        }
    }
}
