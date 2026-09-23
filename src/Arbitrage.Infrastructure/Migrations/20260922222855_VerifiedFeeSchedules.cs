using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedFeeSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeeProfiles",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Profile = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeProfiles", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_FeeProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeeSchedules",
                columns: table => new
                {
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    MarketId = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RetrievedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeSchedules", x => new { x.Exchange, x.MarketId });
                    table.ForeignKey(
                        name: "FK_FeeSchedules_CatalogMarkets_Exchange_MarketId",
                        columns: x => new { x.Exchange, x.MarketId },
                        principalTable: "CatalogMarkets",
                        principalColumns: new[] { "Exchange", "NativeId" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeeProfiles");

            migrationBuilder.DropTable(
                name: "FeeSchedules");
        }
    }
}
