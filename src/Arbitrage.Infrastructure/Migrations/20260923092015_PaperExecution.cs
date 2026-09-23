using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperGenerationEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Integrity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperGenerationEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperGenerationEntry_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperGenerationEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperBalanceEntry",
                columns: table => new
                {
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    InitialCash = table.Column<decimal>(type: "TEXT", nullable: false),
                    AvailableCash = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReservedCash = table.Column<decimal>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperBalanceEntry", x => new { x.GenerationId, x.Exchange, x.Currency });
                    table.ForeignKey(
                        name: "FK_PaperBalanceEntry_PaperGenerationEntry_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "PaperGenerationEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperExecutionEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    OpportunityKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperExecutionEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperExecutionEntry_PaperGenerationEntry_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "PaperGenerationEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperExecutionEntry_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperPositionEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    MarketId = table.Column<string>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    CostBasis = table.Column<decimal>(type: "TEXT", nullable: false),
                    Fees = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageEntry = table.Column<decimal>(type: "TEXT", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperPositionEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperPositionEntry_PaperGenerationEntry_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "PaperGenerationEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperLegEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    MarketId = table.Column<string>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Notional = table.Column<decimal>(type: "TEXT", nullable: false),
                    Fees = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperLegEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperLegEntry_PaperExecutionEntry_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "PaperExecutionEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperTransactionEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTransactionEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperTransactionEntry_PaperExecutionEntry_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "PaperExecutionEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperTransactionEntry_PaperGenerationEntry_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "PaperGenerationEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperFillEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LegId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FillJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperFillEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperFillEntry_PaperLegEntry_LegId",
                        column: x => x.LegId,
                        principalTable: "PaperLegEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperLedgerEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    AvailableDelta = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReservedDelta = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperLedgerEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperLedgerEntry_PaperTransactionEntry_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "PaperTransactionEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_ActorId",
                table: "PaperExecutionEntry",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_GenerationId",
                table: "PaperExecutionEntry",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_CreatedAt",
                table: "PaperExecutionEntry",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperExecutionEntry_WorkspaceId_RequestId",
                table: "PaperExecutionEntry",
                columns: new[] { "WorkspaceId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperFillEntry_LegId",
                table: "PaperFillEntry",
                column: "LegId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperGenerationEntry_ActorId",
                table: "PaperGenerationEntry",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperGenerationEntry_WorkspaceId",
                table: "PaperGenerationEntry",
                column: "WorkspaceId",
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaperLedgerEntry_TransactionId",
                table: "PaperLedgerEntry",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperLegEntry_ExecutionId",
                table: "PaperLegEntry",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperPositionEntry_GenerationId_Exchange_MarketId_InstrumentId_Outcome",
                table: "PaperPositionEntry",
                columns: new[] { "GenerationId", "Exchange", "MarketId", "InstrumentId", "Outcome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperTransactionEntry_ExecutionId",
                table: "PaperTransactionEntry",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperTransactionEntry_GenerationId",
                table: "PaperTransactionEntry",
                column: "GenerationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperBalanceEntry");

            migrationBuilder.DropTable(
                name: "PaperFillEntry");

            migrationBuilder.DropTable(
                name: "PaperLedgerEntry");

            migrationBuilder.DropTable(
                name: "PaperPositionEntry");

            migrationBuilder.DropTable(
                name: "PaperLegEntry");

            migrationBuilder.DropTable(
                name: "PaperTransactionEntry");

            migrationBuilder.DropTable(
                name: "PaperExecutionEntry");

            migrationBuilder.DropTable(
                name: "PaperGenerationEntry");
        }
    }
}
