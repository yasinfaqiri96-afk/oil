using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingImportUniqueKeyAndParentContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportUniqueKey",
                table: "LoadingRegisters",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentContractId",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoadingRegisters_ImportUniqueKey",
                table: "LoadingRegisters",
                column: "ImportUniqueKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_ParentContractId",
                table: "Contracts",
                column: "ParentContractId");

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Contracts_ParentContractId",
                table: "Contracts",
                column: "ParentContractId",
                principalTable: "Contracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Contracts_ParentContractId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_LoadingRegisters_ImportUniqueKey",
                table: "LoadingRegisters");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_ParentContractId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ImportUniqueKey",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "ParentContractId",
                table: "Contracts");
        }
    }
}
