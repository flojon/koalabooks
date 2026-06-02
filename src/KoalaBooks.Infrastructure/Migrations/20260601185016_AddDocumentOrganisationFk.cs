using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentOrganisationFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Documents_OrganisationId",
                table: "Documents",
                column: "OrganisationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Organisations_OrganisationId",
                table: "Documents",
                column: "OrganisationId",
                principalTable: "Organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Organisations_OrganisationId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_OrganisationId",
                table: "Documents");
        }
    }
}
