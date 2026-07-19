using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineScanCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineScanCandidates",
                columns: table => new
                {
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GameId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SuggestedPath = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineScanCandidates", x => new { x.MachineId, x.GameId });
                    table.ForeignKey(
                        name: "FK_MachineScanCandidates_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineScanCandidates_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineScanCandidates_GameId",
                table: "MachineScanCandidates",
                column: "GameId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineScanCandidates");
        }
    }
}
