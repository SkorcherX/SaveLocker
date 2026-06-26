using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalGameSync.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRetainVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetainVersions",
                table: "Games",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetainVersions",
                table: "Games");
        }
    }
}
