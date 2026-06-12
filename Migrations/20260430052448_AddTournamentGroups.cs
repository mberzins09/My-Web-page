using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTournamentGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Games",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TournamentGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Event = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TournamentGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupTeams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTeams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupTeams_TournamentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TournamentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Games_GroupId",
                table: "Games",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupTeams_GroupId",
                table: "GroupTeams",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Games_TournamentGroups_GroupId",
                table: "Games",
                column: "GroupId",
                principalTable: "TournamentGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Games_TournamentGroups_GroupId",
                table: "Games");

            migrationBuilder.DropTable(
                name: "GroupTeams");

            migrationBuilder.DropTable(
                name: "TournamentGroups");

            migrationBuilder.DropIndex(
                name: "IX_Games_GroupId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Games");
        }
    }
}
