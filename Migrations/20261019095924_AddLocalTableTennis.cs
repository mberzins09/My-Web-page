using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalTableTennis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayersTT",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Surname = table.Column<string>(type: "TEXT", nullable: false),
                    User_Id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayersTT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayersTT_Users_User_Id",
                        column: x => x.User_Id,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TournamentSeason",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TournamentSeason", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TournamentsTT",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentName = table.Column<string>(type: "TEXT", nullable: false),
                    TournamentDateTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    TournamentSeason_Id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TournamentsTT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TournamentsTT_TournamentSeason_TournamentSeason_Id",
                        column: x => x.TournamentSeason_Id,
                        principalTable: "TournamentSeason",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GamesTT",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tournament_id = table.Column<int>(type: "INTEGER", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    player1_id = table.Column<int>(type: "INTEGER", nullable: false),
                    player2_id = table.Column<int>(type: "INTEGER", nullable: false),
                    player1_sets = table.Column<int>(type: "INTEGER", nullable: false),
                    player2_sets = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamesTT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GamesTT_PlayersTT_player1_id",
                        column: x => x.player1_id,
                        principalTable: "PlayersTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GamesTT_PlayersTT_player2_id",
                        column: x => x.player2_id,
                        principalTable: "PlayersTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GamesTT_TournamentsTT_tournament_id",
                        column: x => x.tournament_id,
                        principalTable: "TournamentsTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamesTT_player1_id",
                table: "GamesTT",
                column: "player1_id");

            migrationBuilder.CreateIndex(
                name: "IX_GamesTT_player2_id",
                table: "GamesTT",
                column: "player2_id");

            migrationBuilder.CreateIndex(
                name: "IX_GamesTT_tournament_id",
                table: "GamesTT",
                column: "tournament_id");

            migrationBuilder.CreateIndex(
                name: "IX_PlayersTT_User_Id",
                table: "PlayersTT",
                column: "User_Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TournamentsTT_TournamentSeason_Id",
                table: "TournamentsTT",
                column: "TournamentSeason_Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamesTT");

            migrationBuilder.DropTable(
                name: "PlayersTT");

            migrationBuilder.DropTable(
                name: "TournamentsTT");

            migrationBuilder.DropTable(
                name: "TournamentSeason");
        }
    }
}
