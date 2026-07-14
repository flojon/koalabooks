using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentExtractionStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExtractionStatus",
                table: "Documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // One-time backfill: every document that existed before this column was added
            // already ran through the old synchronous extraction path in full (successfully
            // or not) — mark it Completed. New rows explicitly set Pending in code; the
            // column's schema default of 0 (Pending) above is only a safety net for future
            // code paths that forget to set it, not a mechanism for this backfill.
            migrationBuilder.Sql("UPDATE \"Documents\" SET \"ExtractionStatus\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtractionStatus",
                table: "Documents");
        }
    }
}
