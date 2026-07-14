using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentXminConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No DDL: "xmin" is Postgres' built-in per-row system column, already present
            // on every table. This migration only teaches the EF model that "Documents"
            // now maps it as a concurrency token — running the auto-generated AddColumn
            // here would fail against Postgres with "column xmin already exists".
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
