using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class LocalTTGroupsBracketsSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupCount",
                table: "TournamentsTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QualifiersPerGroup",
                table: "TournamentsTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SetsToWinGroup",
                table: "TournamentsTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SetsToWinPlayoff",
                table: "TournamentsTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "player2_sets",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "player2_id",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "player1_sets",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "player1_id",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupNumber",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "GamesTT",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameSetsTT",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameId = table.Column<int>(type: "INTEGER", nullable: false),
                    SetNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Player1Points = table.Column<int>(type: "INTEGER", nullable: false),
                    Player2Points = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSetsTT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameSetsTT_GamesTT_GameId",
                        column: x => x.GameId,
                        principalTable: "GamesTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TournamentPlayersTT",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TournamentId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Seed = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TournamentPlayersTT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TournamentPlayersTT_PlayersTT_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "PlayersTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TournamentPlayersTT_TournamentsTT_TournamentId",
                        column: x => x.TournamentId,
                        principalTable: "TournamentsTT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameSetsTT_GameId_SetNumber",
                table: "GameSetsTT",
                columns: new[] { "GameId", "SetNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TournamentPlayersTT_PlayerId",
                table: "TournamentPlayersTT",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_TournamentPlayersTT_TournamentId_PlayerId",
                table: "TournamentPlayersTT",
                columns: new[] { "TournamentId", "PlayerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameSetsTT");

            migrationBuilder.DropTable(
                name: "TournamentPlayersTT");

            migrationBuilder.DropColumn(
                name: "GroupCount",
                table: "TournamentsTT");

            migrationBuilder.DropColumn(
                name: "QualifiersPerGroup",
                table: "TournamentsTT");

            migrationBuilder.DropColumn(
                name: "SetsToWinGroup",
                table: "TournamentsTT");

            migrationBuilder.DropColumn(
                name: "SetsToWinPlayoff",
                table: "TournamentsTT");

            migrationBuilder.DropColumn(
                name: "GroupNumber",
                table: "GamesTT");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "GamesTT");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "GamesTT");

            migrationBuilder.AlterColumn<int>(
                name: "player2_sets",
                table: "GamesTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "player2_id",
                table: "GamesTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "player1_sets",
                table: "GamesTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "player1_id",
                table: "GamesTT",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
