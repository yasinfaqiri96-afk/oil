using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PTGOilSystem.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRubSettlementSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountRubAtRubLock",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountUsdAtRubLock",
                table: "LoadingRegisters",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RubPerUsdRate",
                table: "LoadingRegisters",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RubRateDate",
                table: "LoadingRegisters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RubRateLockedAtUtc",
                table: "LoadingRegisters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubRateLockedByUserName",
                table: "LoadingRegisters",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubRateSource",
                table: "LoadingRegisters",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RubRateStatus",
                table: "LoadingRegisters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SettlementCurrencyCode",
                table: "LoadingRegisters",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "ContractRubPerUsdRate",
                table: "Contracts",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContractRubRateDate",
                table: "Contracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContractRubRateSource",
                table: "Contracts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RubRatePolicy",
                table: "Contracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SettlementCurrencyCode",
                table: "Contracts",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "USD");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountRubAtRubLock",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "AmountUsdAtRubLock",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubPerUsdRate",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubRateDate",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubRateLockedAtUtc",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubRateLockedByUserName",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubRateSource",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "RubRateStatus",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "SettlementCurrencyCode",
                table: "LoadingRegisters");

            migrationBuilder.DropColumn(
                name: "ContractRubPerUsdRate",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ContractRubRateDate",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ContractRubRateSource",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "RubRatePolicy",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "SettlementCurrencyCode",
                table: "Contracts");
        }
    }
}
