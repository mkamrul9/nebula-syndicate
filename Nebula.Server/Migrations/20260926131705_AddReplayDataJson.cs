using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayDataJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplayDataJson",
                table: "MatchRecords",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayDataJson",
                table: "MatchRecords");
        }
    }
}
