using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fserp.FleetOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LimitVehiclePlateNumberLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "plate_number",
                schema: "fleet",
                table: "vehicles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "plate_number",
                schema: "fleet",
                table: "vehicles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }
    }
}
