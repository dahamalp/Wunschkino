using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWishVotePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "WishVotes",
                type: "INTEGER",
                nullable: false,
                // 1 = Normal: Bestehende Stimmen behalten die Mitte. Das Modell setzt die Stufe immer selbst, der Standard gilt nur für vorhandene Zeilen.
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "WishVotes");
        }
    }
}
