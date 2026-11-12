using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentMatching.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyJobTenantColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastBatchDate",
                table: "tenants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRefillAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "LastBatchDate",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "LastRefillAt",
                table: "tenants");
        }
    }
}
