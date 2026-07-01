using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalEntryStatusAndReversalLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceJournalEntryId",
                table: "JournalEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "JournalEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(@"UPDATE ""JournalEntries"" SET ""Status"" = 1 WHERE ""IsPosted"" = true;");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_SourceJournalEntryId",
                table: "JournalEntries",
                column: "SourceJournalEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_JournalEntries_SourceJournalEntryId",
                table: "JournalEntries",
                column: "SourceJournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_JournalEntries_SourceJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_SourceJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "SourceJournalEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "JournalEntries");
        }
    }
}
