using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaperReliabilityCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaperReliabilityCampaignEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    PolicyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PolicyFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceGapDetected = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvariantViolationDetected = table.Column<bool>(type: "INTEGER", nullable: false),
                    EventRetentionTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CountersJson = table.Column<string>(type: "TEXT", nullable: false),
                    TriggersJson = table.Column<string>(type: "TEXT", nullable: false),
                    LatestEvaluationId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperReliabilityCampaignEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperReliabilityCampaignEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperWriterOrderEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProofJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperWriterOrderEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperWriterOrderEntry_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperReliabilityEvaluationEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperReliabilityEvaluationEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperReliabilityEvaluationEntry_PaperReliabilityCampaignEntry_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "PaperReliabilityCampaignEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperReliabilityEventEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WriterOrder = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperReliabilityEventEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperReliabilityEventEntry_PaperReliabilityCampaignEntry_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "PaperReliabilityCampaignEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaperReliabilityIntervalEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BackendId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartOrder = table.Column<long>(type: "INTEGER", nullable: false),
                    EndOrder = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperReliabilityIntervalEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperReliabilityIntervalEntry_PaperReliabilityCampaignEntry_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "PaperReliabilityCampaignEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityCampaignEntry_WorkspaceId_StartedAt",
                table: "PaperReliabilityCampaignEntry",
                columns: new[] { "WorkspaceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityCampaignEntry_WorkspaceId_State",
                table: "PaperReliabilityCampaignEntry",
                columns: new[] { "WorkspaceId", "State" },
                unique: true,
                filter: "State = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityEvaluationEntry_CampaignId_At",
                table: "PaperReliabilityEvaluationEntry",
                columns: new[] { "CampaignId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityEventEntry_CampaignId_At_Id",
                table: "PaperReliabilityEventEntry",
                columns: new[] { "CampaignId", "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityEventEntry_CampaignId_WriterOrder",
                table: "PaperReliabilityEventEntry",
                columns: new[] { "CampaignId", "WriterOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperReliabilityIntervalEntry_CampaignId_StartedAt",
                table: "PaperReliabilityIntervalEntry",
                columns: new[] { "CampaignId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperWriterOrderEntry_WorkspaceId_Id",
                table: "PaperWriterOrderEntry",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperWriterOrderEntry_WorkspaceId_Kind_ReferenceId",
                table: "PaperWriterOrderEntry",
                columns: new[] { "WorkspaceId", "Kind", "ReferenceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaperReliabilityEvaluationEntry");

            migrationBuilder.DropTable(
                name: "PaperReliabilityEventEntry");

            migrationBuilder.DropTable(
                name: "PaperReliabilityIntervalEntry");

            migrationBuilder.DropTable(
                name: "PaperWriterOrderEntry");

            migrationBuilder.DropTable(
                name: "PaperReliabilityCampaignEntry");
        }
    }
}
