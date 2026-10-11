using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWishActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WishActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WishActions_TimeUtc",
                table: "WishActions",
                column: "TimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WishActions_UserId_TimeUtc",
                table: "WishActions",
                columns: new[] { "UserId", "TimeUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WishActions");
        }
    }
}
