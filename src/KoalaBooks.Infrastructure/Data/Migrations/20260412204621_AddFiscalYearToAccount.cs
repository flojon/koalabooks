using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalYearToAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_AccountNumber",
                table: "Accounts");

            migrationBuilder.AddColumn<int>(
                name: "FiscalYearId",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_FiscalYearId_AccountNumber",
                table: "Accounts",
                columns: new[] { "FiscalYearId", "AccountNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_FiscalYears_FiscalYearId",
                table: "Accounts",
                column: "FiscalYearId",
                principalTable: "FiscalYears",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_FiscalYears_FiscalYearId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_FiscalYearId_AccountNumber",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FiscalYearId",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_AccountNumber",
                table: "Accounts",
                column: "AccountNumber",
                unique: true);
        }
    }
}
