using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arbitrage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PublicMarketCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogMarkets",
                columns: table => new
                {
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    NativeId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Environment = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesId = table.Column<string>(type: "TEXT", nullable: true),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true),
                    Classification = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    PrimaryTag = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NativeStatus = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OutcomesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    OpenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CloseAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpectedResolutionAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Rules = table.Column<string>(type: "TEXT", nullable: true),
                    SourceReference = table.Column<string>(type: "TEXT", nullable: true),
                    FirstRetrievedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RetrievedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsIncomplete = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogMarkets", x => new { x.Exchange, x.NativeId });
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentScope = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Pages = table.Column<int>(type: "INTEGER", nullable: false),
                    Observed = table.Column<int>(type: "INTEGER", nullable: false),
                    Malformed = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    RetryAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogTags",
                columns: table => new
                {
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    NativeId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTags", x => new { x.Exchange, x.NativeId, x.Tag });
                    table.ForeignKey(
                        name: "FK_CatalogTags_CatalogMarkets_Exchange_NativeId",
                        columns: x => new { x.Exchange, x.NativeId },
                        principalTable: "CatalogMarkets",
                        principalColumns: new[] { "Exchange", "NativeId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogMarkets_Exchange_PrimaryTag_NativeId",
                table: "CatalogMarkets",
                columns: new[] { "Exchange", "PrimaryTag", "NativeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogMarkets_Exchange_Status_Title_NativeId",
                table: "CatalogMarkets",
                columns: new[] { "Exchange", "Status", "Title", "NativeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogMarkets_RetrievedAt",
                table: "CatalogMarkets",
                column: "RetrievedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTags_Tag",
                table: "CatalogTags",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryRuns_Exchange_StartedAt",
                table: "DiscoveryRuns",
                columns: new[] { "Exchange", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryRuns_Exchange_State",
                table: "DiscoveryRuns",
                columns: new[] { "Exchange", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogTags");

            migrationBuilder.DropTable(
                name: "DiscoveryRuns");

            migrationBuilder.DropTable(
                name: "CatalogMarkets");
        }
    }
}
