using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SaveLocker.Server.Data;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260723231500_AddConflictPolicy")]
    /// <inheritdoc />
    public partial class AddConflictPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0 = Manual (the existing behaviour): existing games keep their current semantics.
            migrationBuilder.AddColumn<int>(
                name: "ConflictPolicy",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PreferredMachineId",
                table: "Games",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConflictPolicy",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "PreferredMachineId",
                table: "Games");
        }
    }
}
