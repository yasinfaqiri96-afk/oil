using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleAccessSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedNavigationItems",
                table: "Roles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageData",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageUsers",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            const string allSections = "Dashboard,Contracts,Operations,Inventory,OperationalAssets,Sales,CashAccounts,Payments,Reports,Partners,BaseDefinitions,Rates,Management";
            const string businessSections = "Dashboard,Contracts,Operations,Inventory,OperationalAssets,Sales,CashAccounts,Payments,Reports,Partners,BaseDefinitions,Rates";

            migrationBuilder.Sql($"""
                UPDATE "Roles"
                SET "AllowedNavigationItems" = '{allSections}',
                    "CanManageData" = TRUE,
                    "CanManageUsers" = TRUE
                WHERE "Name" = 'Admin';
                """);

            migrationBuilder.Sql($"""
                UPDATE "Roles"
                SET "AllowedNavigationItems" = '{businessSections}',
                    "CanManageData" = TRUE
                WHERE "Name" IN ('Manager', 'Operator');
                """);

            migrationBuilder.Sql($"""
                UPDATE "Roles"
                SET "AllowedNavigationItems" = '{businessSections}'
                WHERE "Name" = 'Viewer';
                """);

            migrationBuilder.Sql("""
                UPDATE "Roles"
                SET "AllowedNavigationItems" = 'Dashboard'
                WHERE "AllowedNavigationItems" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedNavigationItems",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CanManageData",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "CanManageUsers",
                table: "Roles");
        }
    }
}
