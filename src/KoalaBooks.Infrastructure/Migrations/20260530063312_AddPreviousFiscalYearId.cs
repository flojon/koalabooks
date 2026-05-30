using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviousFiscalYearId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviousFiscalYearId",
                table: "FiscalYears",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_PreviousFiscalYearId",
                table: "FiscalYears",
                column: "PreviousFiscalYearId");

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYears_FiscalYears_PreviousFiscalYearId",
                table: "FiscalYears",
                column: "PreviousFiscalYearId",
                principalTable: "FiscalYears",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiscalYears_FiscalYears_PreviousFiscalYearId",
                table: "FiscalYears");

            migrationBuilder.DropIndex(
                name: "IX_FiscalYears_PreviousFiscalYearId",
                table: "FiscalYears");

            migrationBuilder.DropColumn(
                name: "PreviousFiscalYearId",
                table: "FiscalYears");
        }
    }
}
