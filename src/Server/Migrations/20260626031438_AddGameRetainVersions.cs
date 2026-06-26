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
            // IF NOT EXISTS guards against databases where the column was added by the
            // pre-migration manual workaround in Program.cs (SQLite 3.37+, bundled in EF9).
            migrationBuilder.Sql("""ALTER TABLE "Games" ADD COLUMN IF NOT EXISTS "RetainVersions" INTEGER NULL;""");
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
