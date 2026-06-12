using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MartinsWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddOvertimeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PredictedIsOvertime",
                table: "Predictions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOvertime",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PredictedIsOvertime",
                table: "Predictions");

            migrationBuilder.DropColumn(
                name: "IsOvertime",
                table: "Games");
        }
    }
}
