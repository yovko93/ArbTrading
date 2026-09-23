using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResolutionId",
                table: "PaperTransactionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedPnl",
                table: "PaperPositionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PaperPositionEntry",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SettledAt",
                table: "PaperPositionEntry",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SettlementPayout",
                table: "PaperPositionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettlementResolutionId",
                table: "PaperPositionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "PaperPositionEntry",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PaperGenerationEntry",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SettledAt",
                table: "PaperExecutionEntry",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementJson",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementMarketsJson",
                table: "PaperExecutionEntry",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            // Preserve locally known instrument definitions for pre-04B executions. No outcome is
            // resolved here. Missing/unsupported definitions remain fail-closed at preview time.
            migrationBuilder.Sql("""
                UPDATE PaperExecutionEntry AS e SET SettlementMarketsJson = (
                  SELECT json_group_array(json_object('Exchange', m.Exchange, 'MarketId', m.NativeId,
                    'Title', m.Title, 'Structure', m.Classification, 'Outcomes', json((
                      SELECT json_group_array(json_object('InstrumentId',
                        CASE WHEN m.Exchange = 'Kalshi' THEN lower(json_extract(o.value, '$.Label'))
                        ELSE json_extract(o.value, '$.NativeTokenId') END,
                        'Outcome', json_extract(o.value, '$.Label'), 'PayoutPerShare', 0))
                      FROM json_each(m.OutcomesJson) o))))
                  FROM CatalogMarkets m WHERE EXISTS (
                    SELECT 1 FROM PaperLegEntry l WHERE l.ExecutionId = e.Id
                    AND l.Exchange = m.Exchange AND l.MarketId = m.NativeId))
                """);

            migrationBuilder.CreateTable(
                name: "PaperResolutionEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProofFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    MarketId = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperResolutionEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperResolutionEntry_PaperGenerationEntry_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "PaperGenerationEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperResolutionEntry_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaperResolutionEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperResolutionOutcomeEntry",
                columns: table => new
                {
                    ResolutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    PayoutPerShare = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperResolutionOutcomeEntry", x => new { x.ResolutionId, x.InstrumentId });
                    table.ForeignKey(
                        name: "FK_PaperResolutionOutcomeEntry_PaperResolutionEntry_ResolutionId",
                        column: x => x.ResolutionId,
                        principalTable: "PaperResolutionEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperTransactionEntry_ResolutionId",
                table: "PaperTransactionEntry",
                column: "ResolutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperPositionEntry_SettlementResolutionId",
                table: "PaperPositionEntry",
                column: "SettlementResolutionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperResolutionEntry_ActorId",
                table: "PaperResolutionEntry",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperResolutionEntry_GenerationId_Exchange_MarketId",
                table: "PaperResolutionEntry",
                columns: new[] { "GenerationId", "Exchange", "MarketId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperResolutionEntry_WorkspaceId_RequestId",
                table: "PaperResolutionEntry",
                columns: new[] { "WorkspaceId", "RequestId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PaperPositionEntry_PaperResolutionEntry_SettlementResolutionId",
                table: "PaperPositionEntry",
                column: "SettlementResolutionId",
                principalTable: "PaperResolutionEntry",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaperTransactionEntry_PaperResolutionEntry_ResolutionId",
                table: "PaperTransactionEntry",
                column: "ResolutionId",
                principalTable: "PaperResolutionEntry",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaperPositionEntry_PaperResolutionEntry_SettlementResolutionId",
                table: "PaperPositionEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_PaperTransactionEntry_PaperResolutionEntry_ResolutionId",
                table: "PaperTransactionEntry");

            migrationBuilder.DropTable(
                name: "PaperResolutionOutcomeEntry");

            migrationBuilder.DropTable(
                name: "PaperResolutionEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperTransactionEntry_ResolutionId",
                table: "PaperTransactionEntry");

            migrationBuilder.DropIndex(
                name: "IX_PaperPositionEntry_SettlementResolutionId",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "ResolutionId",
                table: "PaperTransactionEntry");

            migrationBuilder.DropColumn(
                name: "RealizedPnl",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "SettledAt",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "SettlementPayout",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "SettlementResolutionId",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "PaperPositionEntry");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PaperGenerationEntry");

            migrationBuilder.DropColumn(
                name: "SettledAt",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "SettlementJson",
                table: "PaperExecutionEntry");

            migrationBuilder.DropColumn(
                name: "SettlementMarketsJson",
                table: "PaperExecutionEntry");
        }
    }
}
