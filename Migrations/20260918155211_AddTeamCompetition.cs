using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamCompetition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeptemberPlayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApiPlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Surname = table.Column<string>(type: "TEXT", nullable: false),
                    PointsWithBonus = table.Column<decimal>(type: "TEXT", nullable: false),
                    IsManualEdit = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeptemberPlayers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamName = table.Column<string>(type: "TEXT", nullable: false),
                    AlreadyBuilt = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamPlayerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiPlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Surname = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamPlayerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamPlayerEntries_TeamEntries_TeamEntryId",
                        column: x => x.TeamEntryId,
                        principalTable: "TeamEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamPlayerEntries_TeamEntryId",
                table: "TeamPlayerEntries",
                column: "TeamEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeptemberPlayers");

            migrationBuilder.DropTable(
                name: "TeamPlayerEntries");

            migrationBuilder.DropTable(
                name: "TeamEntries");
        }
    }
}
