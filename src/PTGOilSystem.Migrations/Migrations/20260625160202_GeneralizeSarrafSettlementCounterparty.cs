using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeSarrafSettlementCounterparty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CounterpartyType",
                table: "SarrafSettlements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CustomerId",
                table: "SarrafSettlements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "SarrafSettlements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ServiceProviderId",
                table: "SarrafSettlements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_CustomerId",
                table: "SarrafSettlements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SarrafSettlements_ServiceProviderId",
                table: "SarrafSettlements",
                column: "ServiceProviderId");

            migrationBuilder.AddForeignKey(
                name: "FK_SarrafSettlements_Customers_CustomerId",
                table: "SarrafSettlements",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SarrafSettlements_ServiceProviders_ServiceProviderId",
                table: "SarrafSettlements",
                column: "ServiceProviderId",
                principalTable: "ServiceProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SarrafSettlements_Customers_CustomerId",
                table: "SarrafSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_SarrafSettlements_ServiceProviders_ServiceProviderId",
                table: "SarrafSettlements");

            migrationBuilder.DropIndex(
                name: "IX_SarrafSettlements_CustomerId",
                table: "SarrafSettlements");

            migrationBuilder.DropIndex(
                name: "IX_SarrafSettlements_ServiceProviderId",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "CounterpartyType",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "SarrafSettlements");

            migrationBuilder.DropColumn(
                name: "ServiceProviderId",
                table: "SarrafSettlements");
        }
    }
}
