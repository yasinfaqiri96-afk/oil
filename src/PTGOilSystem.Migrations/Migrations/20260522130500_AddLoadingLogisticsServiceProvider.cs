using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingLogisticsServiceProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FreightRateUsdPerMt",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogisticsServiceProviderId",
                table: "LoadingRegisters",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingRegisters_LogisticsServiceProviderId",
                table: "LoadingRegisters",
                column: "LogisticsServiceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingRegisters_ServiceProviders_LogisticsServiceProviderId",
                table: "LoadingRegisters",
                column: "LogisticsServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LoadingRegisters_ServiceProviders_LogisticsServiceProviderId",
                table: "LoadingRegisters");

            migrationBuilder.DropIndex(
                name: "IX_LoadingRegisters_LogisticsServiceProviderId",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "FreightRateUsdPerMt",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "LogisticsServiceProviderId",
                table: "LoadingRegisters");
        }
    }
}
