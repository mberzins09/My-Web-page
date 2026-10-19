using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTournamentSportType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SportType",
                table: "Tournaments",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SportType",
                table: "Tournaments");
        }
    }
}
