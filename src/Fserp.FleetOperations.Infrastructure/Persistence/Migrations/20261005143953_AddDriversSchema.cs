using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fserp.FleetOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDriversSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "drivers");

            migrationBuilder.CreateTable(
                name: "drivers",
                schema: "drivers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operational_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    committed_mission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drivers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qualifications",
                schema: "drivers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    driver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qualifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_qualifications_drivers_driver_id",
                        column: x => x.driver_id,
                        principalSchema: "drivers",
                        principalTable: "drivers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_qualifications_driver_id_vehicle_type",
                schema: "drivers",
                table: "qualifications",
                columns: new[] { "driver_id", "vehicle_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qualifications",
                schema: "drivers");

            migrationBuilder.DropTable(
                name: "drivers",
                schema: "drivers");
        }
    }
}
