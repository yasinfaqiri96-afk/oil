using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingRegisterDriverId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DriverId",
                table: "LoadingRegisters",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingRegisters_DriverId",
                table: "LoadingRegisters",
                column: "DriverId");

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingRegisters_Drivers_DriverId",
                table: "LoadingRegisters",
                column: "DriverId",
                principalTable: "Drivers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LoadingRegisters_Drivers_DriverId",
                table: "LoadingRegisters");

            migrationBuilder.DropIndex(
                name: "IX_LoadingRegisters_DriverId",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "DriverId",
                table: "LoadingRegisters");
        }
    }
}
