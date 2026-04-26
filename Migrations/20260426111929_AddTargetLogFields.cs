using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GCS_Project.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetLogFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlightId",
                table: "TargetLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TargetId",
                table: "TargetLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlightId",
                table: "TargetLogs");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "TargetLogs");
        }
    }
}
