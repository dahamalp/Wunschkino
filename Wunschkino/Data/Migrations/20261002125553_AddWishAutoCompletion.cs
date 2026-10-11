using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wunschkino.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWishAutoCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoCompletion",
                table: "Wishes",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCompletion",
                table: "Wishes");
        }
    }
}
