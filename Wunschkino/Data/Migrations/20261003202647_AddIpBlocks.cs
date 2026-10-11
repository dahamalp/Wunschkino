using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIpBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsPermanent = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastFailureUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpBlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IpBlocks_Address",
                table: "IpBlocks",
                column: "Address",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IpBlocks");
        }
    }
}
