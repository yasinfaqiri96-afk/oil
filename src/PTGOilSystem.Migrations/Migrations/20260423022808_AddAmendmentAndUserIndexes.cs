using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAmendmentAndUserIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractAmendments_ContractId",
                table: "ContractAmendments");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAmendments_ContractId_AmendmentNumber",
                table: "ContractAmendments",
                columns: new[] { "ContractId", "AmendmentNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ContractAmendments_ContractId_AmendmentNumber",
                table: "ContractAmendments");

            migrationBuilder.CreateIndex(
                name: "IX_ContractAmendments_ContractId",
                table: "ContractAmendments",
                column: "ContractId");
        }
    }
}
