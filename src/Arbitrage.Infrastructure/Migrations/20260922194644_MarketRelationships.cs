using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MarketRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RelationshipMetadataBaseFingerprint",
                table: "CatalogMarkets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelationshipMetadataJson",
                table: "CatalogMarkets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "AuditRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarketRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceExchange = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetExchange = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastValidatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PolicyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    TargetFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewReason = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketRelationships_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    Sources = table.Column<int>(type: "INTEGER", nullable: false),
                    Comparisons = table.Column<int>(type: "INTEGER", nullable: false),
                    Written = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Notice = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelationshipJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Dimension = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    Blocking = table.Column<bool>(type: "INTEGER", nullable: false),
                    Contradiction = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelationshipEvidence_MarketRelationships_RelationshipId",
                        column: x => x.RelationshipId,
                        principalTable: "MarketRelationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipOutcomeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelationshipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceOutcomeId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetOutcomeId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipOutcomeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelationshipOutcomeMappings_MarketRelationships_RelationshipId",
                        column: x => x.RelationshipId,
                        principalTable: "MarketRelationships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRelationships_WorkspaceId_SourceExchange_SourceId_TargetExchange_TargetId",
                table: "MarketRelationships",
                columns: new[] { "WorkspaceId", "SourceExchange", "SourceId", "TargetExchange", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketRelationships_WorkspaceId_State",
                table: "MarketRelationships",
                columns: new[] { "WorkspaceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRelationships_WorkspaceId_TargetExchange_TargetId",
                table: "MarketRelationships",
                columns: new[] { "WorkspaceId", "TargetExchange", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketRelationships_WorkspaceId_Type",
                table: "MarketRelationships",
                columns: new[] { "WorkspaceId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipEvidence_RelationshipId",
                table: "RelationshipEvidence",
                column: "RelationshipId");

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipJobs_WorkspaceId_StartedAt",
                table: "RelationshipJobs",
                columns: new[] { "WorkspaceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipOutcomeMappings_RelationshipId",
                table: "RelationshipOutcomeMappings",
                column: "RelationshipId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelationshipEvidence");

            migrationBuilder.DropTable(
                name: "RelationshipJobs");

            migrationBuilder.DropTable(
                name: "RelationshipOutcomeMappings");

            migrationBuilder.DropTable(
                name: "MarketRelationships");

            migrationBuilder.DropColumn(
                name: "RelationshipMetadataBaseFingerprint",
                table: "CatalogMarkets");

            migrationBuilder.DropColumn(
                name: "RelationshipMetadataJson",
                table: "CatalogMarkets");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "AuditRecords");
        }
    }
}
