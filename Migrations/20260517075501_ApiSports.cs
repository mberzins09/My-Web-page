using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class ApiSports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiSportsConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: true),
                    LeagueName = table.Column<string>(type: "TEXT", nullable: false),
                    SeasonYear = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiSportsConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiSportsConfigs_Tournaments_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "Tournaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: false),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    RunAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiSyncLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiTeamMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalTeamName = table.Column<string>(type: "TEXT", nullable: false),
                    ApiTeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiTeamName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTeamMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTeamMappings_Tournaments_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "Tournaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiSportsConfigs_TournamentId",
                table: "ApiSportsConfigs",
                column: "TournamentId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTeamMappings_TournamentId",
                table: "ApiTeamMappings",
                column: "TournamentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiSportsConfigs");

            migrationBuilder.DropTable(
                name: "ApiSyncLogs");

            migrationBuilder.DropTable(
                name: "ApiTeamMappings");
        }
    }
}
