using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Nebula.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSyndicatePass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CosmeticItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    PriceInCoins = table.Column<int>(type: "integer", nullable: false),
                    AssetRef = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CosmeticItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerClaimedRewards",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonTierId = table.Column<int>(type: "integer", nullable: false),
                    ClaimedFree = table.Column<bool>(type: "boolean", nullable: false),
                    ClaimedPremium = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerClaimedRewards", x => new { x.PlayerId, x.SeasonTierId });
                });

            migrationBuilder.CreateTable(
                name: "PlayerSeasonProgress",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<int>(type: "integer", nullable: false),
                    TotalXP = table.Column<int>(type: "integer", nullable: false),
                    HasPremiumPass = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSeasonProgress", x => new { x.PlayerId, x.SeasonId });
                });

            migrationBuilder.CreateTable(
                name: "PremiumLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ReferenceId = table.Column<string>(type: "text", nullable: false),
                    TimestampUTC = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PremiumLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PremiumLedgerEntries_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Seasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    StartDateUTC = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDateUTC = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerCosmetics",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CosmeticItemId = table.Column<int>(type: "integer", nullable: false),
                    AcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEquipped = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerCosmetics", x => new { x.PlayerId, x.CosmeticItemId });
                    table.ForeignKey(
                        name: "FK_PlayerCosmetics_CosmeticItems_CosmeticItemId",
                        column: x => x.CosmeticItemId,
                        principalTable: "CosmeticItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayerCosmetics_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeasonTiers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeasonId = table.Column<int>(type: "integer", nullable: false),
                    RequiredXP = table.Column<int>(type: "integer", nullable: false),
                    FreeCosmeticId = table.Column<int>(type: "integer", nullable: true),
                    PremiumCosmeticId = table.Column<int>(type: "integer", nullable: true),
                    PremiumCurrencyReward = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonTiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonTiers_Seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CosmeticItems",
                columns: new[] { "Id", "AssetRef", "Description", "Name", "PriceInCoins", "Type" },
                values: new object[,]
                {
                    { 1, "drone-neon-crimson", "A red neon glowing drone skin.", "Neon Crimson", 500, 1 },
                    { 2, "avatar-gold-elite", "Premium gold plated avatar.", "Gold Elite", 1000, 0 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerCosmetics_CosmeticItemId",
                table: "PlayerCosmetics",
                column: "CosmeticItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PremiumLedgerEntries_PlayerId",
                table: "PremiumLedgerEntries",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonTiers_SeasonId",
                table: "SeasonTiers",
                column: "SeasonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerClaimedRewards");

            migrationBuilder.DropTable(
                name: "PlayerCosmetics");

            migrationBuilder.DropTable(
                name: "PlayerSeasonProgress");

            migrationBuilder.DropTable(
                name: "PremiumLedgerEntries");

            migrationBuilder.DropTable(
                name: "SeasonTiers");

            migrationBuilder.DropTable(
                name: "CosmeticItems");

            migrationBuilder.DropTable(
                name: "Seasons");
        }
    }
}
