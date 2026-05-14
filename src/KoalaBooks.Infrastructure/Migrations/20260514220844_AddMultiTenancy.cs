using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrganisationId",
                table: "FiscalYears",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OrganisationId",
                table: "BankTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OrganisationId",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Organisations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organisations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FiscalYears_OrganisationId",
                table: "FiscalYears",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_OrganisationId",
                table: "BankTransactions",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_OrganisationId",
                table: "AspNetUsers",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_Organisations_Slug",
                table: "Organisations",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Organisations_OrganisationId",
                table: "AspNetUsers",
                column: "OrganisationId",
                principalTable: "Organisations",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_Organisations_OrganisationId",
                table: "BankTransactions",
                column: "OrganisationId",
                principalTable: "Organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FiscalYears_Organisations_OrganisationId",
                table: "FiscalYears",
                column: "OrganisationId",
                principalTable: "Organisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Organisations_OrganisationId",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_Organisations_OrganisationId",
                table: "BankTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_FiscalYears_Organisations_OrganisationId",
                table: "FiscalYears");

            migrationBuilder.DropTable(
                name: "Organisations");

            migrationBuilder.DropIndex(
                name: "IX_FiscalYears_OrganisationId",
                table: "FiscalYears");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_OrganisationId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_OrganisationId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "FiscalYears");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "AspNetUsers");
        }
    }
}
