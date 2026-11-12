using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentMatching.Data.OperationsMigrations
{
    /// <inheritdoc />
    public partial class InitialOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_response_cache",
                columns: table => new
                {
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Response = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    LastHitAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_response_cache", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "daily_usage",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    AiCalls = table.Column<int>(type: "integer", nullable: false),
                    AiCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CacheHits = table.Column<int>(type: "integer", nullable: false),
                    SandboxesRefilled = table.Column<int>(type: "integer", nullable: false),
                    SandboxesDeleted = table.Column<int>(type: "integer", nullable: false),
                    SignInsTurnedAway = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_usage", x => x.Day);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_response_cache");

            migrationBuilder.DropTable(
                name: "daily_usage");
        }
    }
}
