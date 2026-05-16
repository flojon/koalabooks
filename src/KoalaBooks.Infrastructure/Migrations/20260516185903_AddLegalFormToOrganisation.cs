using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalFormToOrganisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegalForm",
                table: "Organisations",
                type: "integer",
                nullable: false,
                defaultValue: 1); // 1 = Aktiebolag
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LegalForm",
                table: "Organisations");
        }
    }
}
