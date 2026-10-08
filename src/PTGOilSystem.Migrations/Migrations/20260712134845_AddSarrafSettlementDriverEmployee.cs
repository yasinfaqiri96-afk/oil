using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSarrafSettlementDriverEmployee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DriverId",
                table: "SarrafSettlements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmployeeId",
                table: "SarrafSettlements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_DriverId",
                table: "SarrafSettlements",
                column: "DriverId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_EmployeeId",
                table: "SarrafSettlements",
                column: "EmployeeId");

            migrationBuilder.AddForeignKey(
                name: "FK_SarrafSettlements_Drivers_DriverId",
                table: "SarrafSettlements",
                column: "DriverId",
                principalTable: "Drivers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SarrafSettlements_Employees_EmployeeId",
                table: "SarrafSettlements",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SarrafSettlements_Drivers_DriverId",
                table: "SarrafSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_SarrafSettlements_Employees_EmployeeId",
                table: "SarrafSettlements");

            migrationBuilder.DropIndex(
                name: "IX_SarrafSettlements_DriverId",
                table: "SarrafSettlements");

            migrationBuilder.DropIndex(
                name: "IX_SarrafSettlements_EmployeeId",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "DriverId",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "SarrafSettlements");
        }
    }
}
