using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingReceiptAllocationTraceDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DestinationLocationId",
                table: "LoadingReceiptAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationName",
                table: "LoadingReceiptAllocations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationReference",
                table: "LoadingReceiptAllocations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationStorageTankId",
                table: "LoadingReceiptAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationTerminalId",
                table: "LoadingReceiptAllocations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "LoadingReceiptAllocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                @"UPDATE ""LoadingReceiptAllocations""
                  SET ""Status"" = 1
                  WHERE ""Destination"" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_Destination",
                table: "LoadingReceiptAllocations",
                column: "Destination");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_DestinationLocationId",
                table: "LoadingReceiptAllocations",
                column: "DestinationLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_DestinationStorageTankId",
                table: "LoadingReceiptAllocations",
                column: "DestinationStorageTankId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_DestinationTerminalId",
                table: "LoadingReceiptAllocations",
                column: "DestinationTerminalId");

            migrationBuilder.CreateIndex(
                name: "IX_LoadingReceiptAllocations_Status",
                table: "LoadingReceiptAllocations",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingReceiptAllocations_Locations_DestinationLocationId",
                table: "LoadingReceiptAllocations",
                column: "DestinationLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingReceiptAllocations_StorageTanks_DestinationStorageTa~",
                table: "LoadingReceiptAllocations",
                column: "DestinationStorageTankId",
                principalTable: "StorageTanks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LoadingReceiptAllocations_Terminals_DestinationTerminalId",
                table: "LoadingReceiptAllocations",
                column: "DestinationTerminalId",
                principalTable: "Terminals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LoadingReceiptAllocations_Locations_DestinationLocationId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropForeignKey(
                name: "FK_LoadingReceiptAllocations_StorageTanks_DestinationStorageTa~",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropForeignKey(
                name: "FK_LoadingReceiptAllocations_Terminals_DestinationTerminalId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_LoadingReceiptAllocations_Destination",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_LoadingReceiptAllocations_DestinationLocationId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_LoadingReceiptAllocations_DestinationStorageTankId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_LoadingReceiptAllocations_DestinationTerminalId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropIndex(
                name: "IX_LoadingReceiptAllocations_Status",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "DestinationLocationId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "DestinationName",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "DestinationReference",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "DestinationStorageTankId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "DestinationTerminalId",
                table: "LoadingReceiptAllocations");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "LoadingReceiptAllocations");
        }
    }
}
