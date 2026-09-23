using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutomationInputStamp",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomationProofJson",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutomationRelationshipId",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutomationSessionId",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "PaperExecutionEntry",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PaperAutomationControlEntry",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsLatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    LatchedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LatchedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ResetAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResetBy = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperAutomationControlEntry", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_PaperAutomationControlEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperAutomationProfileEntry",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperAutomationProfileEntry", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_PaperAutomationProfileEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_AutomationRelationshipId_CreatedAt",
                table: "PaperExecutionEntry",
                columns: new[] { "WorkspaceId", "AutomationRelationshipId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_AutomationSessionId_AutomationInputStamp",
                table: "PaperExecutionEntry",
                columns: new[] { "WorkspaceId", "AutomationSessionId", "AutomationInputStamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_Origin_CreatedAt",
                table: "PaperExecutionEntry",
                columns: new[] { "WorkspaceId", "Origin", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperAutomationControlEntry");

            migrationBuilder.DropTable(
                name: "PaperAutomationProfileEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_AutomationRelationshipId_CreatedAt",
                table: "PaperExecutionEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_AutomationSessionId_AutomationInputStamp",
                table: "PaperExecutionEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_Origin_CreatedAt",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "AutomationInputStamp",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "AutomationProofJson",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "AutomationRelationshipId",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "AutomationSessionId",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "PaperExecutionEntry");
        }
    }
}
