using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWishMetadataFetched : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataFetchedUtc",
                table: "Wishes",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Die TMDB-Daten bestehender Wünsche stammen vom Anlegen; damit läuft die 6-Monats-Frist ab dann.
            migrationBuilder.Sql("UPDATE Wishes SET MetadataFetchedUtc = CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataFetchedUtc",
                table: "Wishes");
        }
    }
}
