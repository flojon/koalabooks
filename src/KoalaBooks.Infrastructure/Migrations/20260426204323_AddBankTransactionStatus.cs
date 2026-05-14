using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBankTransactionStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JournalEntryId",
                table: "BankTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "BankTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_JournalEntryId",
                table: "BankTransactions",
                column: "JournalEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_JournalEntries_JournalEntryId",
                table: "BankTransactions",
                column: "JournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_JournalEntries_JournalEntryId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_JournalEntryId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BankTransactions");
        }
    }
}
