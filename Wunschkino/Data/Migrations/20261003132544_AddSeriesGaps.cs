using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeriesGaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JellyfinId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    HasResult = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ExpectedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PresentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MissingJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesGaps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesGaps_JellyfinId",
                table: "SeriesGaps",
                column: "JellyfinId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesGaps");
        }
    }
}
