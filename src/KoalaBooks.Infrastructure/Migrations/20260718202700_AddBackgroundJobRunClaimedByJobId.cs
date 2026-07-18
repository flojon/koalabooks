using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundJobRunClaimedByJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedByJobId",
                table: "BackgroundJobRuns",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // No DDL for "xmin": it's Postgres' built-in per-row system column, already
            // present on every table (see AddDocumentXminConcurrencyToken). This migration
            // only teaches the EF model that BackgroundJobRuns now maps it as a concurrency
            // token — the auto-generated AddColumn here would fail with "column xmin already exists".
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedByJobId",
                table: "BackgroundJobRuns");
        }
    }
}
