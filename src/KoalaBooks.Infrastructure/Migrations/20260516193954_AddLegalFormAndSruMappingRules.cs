using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalFormAndSruMappingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegalForm",
                table: "Organisations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SruMappingRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LegalForm = table.Column<int>(type: "integer", nullable: false),
                    SruCode = table.Column<int>(type: "integer", nullable: false),
                    RadLabel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AccountPatterns = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Sign = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SruMappingRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SruMappingRules_LegalForm_SruCode",
                table: "SruMappingRules",
                columns: new[] { "LegalForm", "SruCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SruMappingRules");

            migrationBuilder.DropColumn(
                name: "LegalForm",
                table: "Organisations");
        }
    }
}
