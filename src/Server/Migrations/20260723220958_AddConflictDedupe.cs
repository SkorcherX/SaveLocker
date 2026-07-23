using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaveLocker.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddConflictDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 1, not EF's generated 0: a conflict that already exists represents at
            // least one divergent push, and a console reading "0 occurrences" would be a lie.
            migrationBuilder.AddColumn<int>(
                name: "Count",
                table: "Conflicts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeen",
                table: "Conflicts",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "MachineId",
                table: "Conflicts",
                type: "TEXT",
                nullable: true);

            // EF's column default for a non-nullable DateTime is 0001-01-01, which every existing
            // conflict would then display as. Backfill from CreatedAt: for a conflict that predates
            // dedupe those are the same moment by definition, since it only ever recorded one push.
            migrationBuilder.Sql(@"UPDATE ""Conflicts"" SET ""LastSeen"" = ""CreatedAt"";");

            // MachineId is deliberately left null on existing rows rather than guessed. A pre-dedupe
            // conflict is resolved and closed by the same admin action either way, and inventing an
            // owner for it would put a wrong machine name in the console.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Count",
                table: "Conflicts");

            migrationBuilder.DropColumn(
                name: "LastSeen",
                table: "Conflicts");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "Conflicts");
        }
    }
}
