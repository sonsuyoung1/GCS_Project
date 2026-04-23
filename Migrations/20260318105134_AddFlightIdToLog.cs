using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GCS_Project.Migrations
{
    /// <inheritdoc />
    public partial class AddFlightIdToLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlightId",
                table: "FlightLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FlightId",
                table: "FlightLogs");
        }
    }
}