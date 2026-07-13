using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DocumentDataLargeObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "Oid",
                table: "DocumentData",
                type: "oid",
                nullable: true);

            migrationBuilder.Sql(@"UPDATE ""DocumentData"" SET ""Oid"" = lo_from_bytea(0, ""Data"");");

            migrationBuilder.AlterColumn<uint>(
                name: "Oid",
                table: "DocumentData",
                type: "oid",
                nullable: false,
                oldClrType: typeof(uint),
                oldType: "oid",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "DocumentData");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Data",
                table: "DocumentData",
                type: "bytea",
                nullable: true);

            migrationBuilder.Sql(@"UPDATE ""DocumentData"" SET ""Data"" = lo_get(""Oid"");");

            migrationBuilder.AlterColumn<byte[]>(
                name: "Data",
                table: "DocumentData",
                type: "bytea",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.Sql(@"SELECT lo_unlink(""Oid"") FROM ""DocumentData"";");

            migrationBuilder.DropColumn(
                name: "Oid",
                table: "DocumentData");
        }
    }
}
