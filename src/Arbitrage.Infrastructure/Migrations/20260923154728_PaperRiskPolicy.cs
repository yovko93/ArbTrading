using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperRiskPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaperExecutionEntry_GenerationId",
                table: "PaperExecutionEntry");

            migrationBuilder.AddColumn<Guid>(
                name: "RiskPolicyRevision",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskPolicyVersion",
                table: "PaperExecutionEntry",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskProofJson",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaperRiskProfiles",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<Guid>(type: "TEXT", nullable: false),
                    Limits_MinimumCashReserveFraction = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MaximumSingleExecutionDebitFraction = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MaximumOpenCostBasisFraction = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MaximumMarketCostBasisFraction = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MaximumInstrumentCostBasisFraction = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MaximumOpenPositions = table.Column<int>(type: "INTEGER", nullable: false),
                    Limits_MaximumOpenExecutions = table.Column<int>(type: "INTEGER", nullable: false),
                    Limits_MaximumOpenExecutionsPerRelationship = table.Column<int>(type: "INTEGER", nullable: false),
                    Limits_MaximumRequestedQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MinimumFeeAdjustedEdgePerShare = table.Column<decimal>(type: "TEXT", nullable: false),
                    Limits_MinimumFeeAdjustedProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperRiskProfiles", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_PaperRiskProfiles_Users_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperRiskProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperPositionEntry_GenerationId_Status",
                table: "PaperPositionEntry",
                columns: new[] { "GenerationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_GenerationId_State",
                table: "PaperExecutionEntry",
                columns: new[] { "GenerationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperRiskProfiles_UpdatedBy",
                table: "PaperRiskProfiles",
                column: "UpdatedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperRiskProfiles");

            migrationBuilder.DropIndex(
                name: "IX_PaperPositionEntry_GenerationId_Status",
                table: "PaperPositionEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperExecutionEntry_GenerationId_State",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "RiskPolicyRevision",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "RiskPolicyVersion",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "RiskProofJson",
                table: "PaperExecutionEntry");

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_GenerationId",
                table: "PaperExecutionEntry",
                column: "GenerationId");
        }
    }
}
