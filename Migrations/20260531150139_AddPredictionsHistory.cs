using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PredictionsHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: true),
                    TournamentName = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictionsHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredictionsHistories_Tournaments_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "Tournaments",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PredictionsHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PredictionsHistoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    Points = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredictionsHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredictionsHistoryEntries_PredictionsHistories_PredictionsHistoryId",
                        column: x => x.PredictionsHistoryId,
                        principalTable: "PredictionsHistories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PredictionsHistoryEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PredictionsHistories_TournamentId",
                table: "PredictionsHistories",
                column: "TournamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PredictionsHistoryEntries_PredictionsHistoryId",
                table: "PredictionsHistoryEntries",
                column: "PredictionsHistoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PredictionsHistoryEntries_UserId",
                table: "PredictionsHistoryEntries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PredictionsHistoryEntries");

            migrationBuilder.DropTable(
                name: "PredictionsHistories");
        }
    }
}
