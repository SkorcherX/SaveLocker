using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineSavePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineSavePaths",
                columns: table => new
                {
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineSavePaths", x => new { x.MachineId, x.GameId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineSavePaths");
        }
    }
}
