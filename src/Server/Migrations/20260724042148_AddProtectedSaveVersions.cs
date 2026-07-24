using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedSaveVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "SaveVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Protected",
                table: "SaveVersions");
        }
    }
}
