using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupTeamCountryId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CountryId",
                table: "GroupTeams",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountryId",
                table: "GroupTeams");
        }
    }
}
