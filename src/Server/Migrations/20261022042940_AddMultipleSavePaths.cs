using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleSavePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These rows never had foreign keys, and the table rebuild below adds them without checking
            // existing rows. A path for a game or machine deleted before the hand-written cleanup existed
            // would otherwise survive as a row the new constraints say cannot exist.
            migrationBuilder.Sql(
                "DELETE FROM \"MachineSavePaths\" " +
                "WHERE \"GameId\" NOT IN (SELECT \"Id\" FROM \"Games\") " +
                "OR \"MachineId\" NOT IN (SELECT \"Id\" FROM \"Machines\");");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MachineSavePaths",
                table: "MachineSavePaths");

            migrationBuilder.AddColumn<string>(
                name: "PathKey",
                table: "MachineSavePaths",
                type: "TEXT",
                nullable: false,
                defaultValue: "main");

            migrationBuilder.AddColumn<string>(
                name: "IncludeGlobs",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MachineSavePaths",
                table: "MachineSavePaths",
                columns: new[] { "MachineId", "GameId", "PathKey" });

            migrationBuilder.CreateTable(
                name: "GameSavePaths",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    Template = table.Column<string>(type: "TEXT", nullable: true),
                    IncludeGlobs = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameSavePaths", x => new { x.GameId, x.Key });
                    table.ForeignKey(
                        name: "FK_GameSavePaths_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineSavePaths_GameId",
                table: "MachineSavePaths",
                column: "GameId");

            migrationBuilder.AddForeignKey(
                name: "FK_MachineSavePaths_Games_GameId",
                table: "MachineSavePaths",
                column: "GameId",
                principalTable: "Games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MachineSavePaths_Machines_MachineId",
                table: "MachineSavePaths",
                column: "MachineId",
                principalTable: "Machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old key is (MachineId, GameId): only the primary folder's row per pair can survive it.
            migrationBuilder.Sql("DELETE FROM \"MachineSavePaths\" WHERE \"PathKey\" <> 'main';");

            migrationBuilder.DropForeignKey(
                name: "FK_MachineSavePaths_Games_GameId",
                table: "MachineSavePaths");

            migrationBuilder.DropForeignKey(
                name: "FK_MachineSavePaths_Machines_MachineId",
                table: "MachineSavePaths");

            migrationBuilder.DropTable(
                name: "GameSavePaths");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MachineSavePaths",
                table: "MachineSavePaths");

            migrationBuilder.DropIndex(
                name: "IX_MachineSavePaths_GameId",
                table: "MachineSavePaths");

            migrationBuilder.DropColumn(
                name: "PathKey",
                table: "MachineSavePaths");

            migrationBuilder.DropColumn(
                name: "IncludeGlobs",
                table: "Games");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MachineSavePaths",
                table: "MachineSavePaths",
                columns: new[] { "MachineId", "GameId" });
        }
    }
}
