using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KoalaBooks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DocumentInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganisationId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SuggestedType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExtractedDataJson = table.Column<string>(type: "text", nullable: true),
                    ClassifiedType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentCustomerInvoices",
                columns: table => new
                {
                    CustomerInvoicesId = table.Column<int>(type: "integer", nullable: false),
                    DocumentsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentCustomerInvoices", x => new { x.CustomerInvoicesId, x.DocumentsId });
                    table.ForeignKey(
                        name: "FK_DocumentCustomerInvoices_CustomerInvoices_CustomerInvoicesId",
                        column: x => x.CustomerInvoicesId,
                        principalTable: "CustomerInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentCustomerInvoices_Documents_DocumentsId",
                        column: x => x.DocumentsId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentData",
                columns: table => new
                {
                    DocumentId = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentData", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_DocumentData_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentJournalEntries",
                columns: table => new
                {
                    DocumentsId = table.Column<int>(type: "integer", nullable: false),
                    JournalEntriesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentJournalEntries", x => new { x.DocumentsId, x.JournalEntriesId });
                    table.ForeignKey(
                        name: "FK_DocumentJournalEntries_Documents_DocumentsId",
                        column: x => x.DocumentsId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentJournalEntries_JournalEntries_JournalEntriesId",
                        column: x => x.JournalEntriesId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSupplierInvoices",
                columns: table => new
                {
                    DocumentsId = table.Column<int>(type: "integer", nullable: false),
                    SupplierInvoicesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSupplierInvoices", x => new { x.DocumentsId, x.SupplierInvoicesId });
                    table.ForeignKey(
                        name: "FK_DocumentSupplierInvoices_Documents_DocumentsId",
                        column: x => x.DocumentsId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentSupplierInvoices_SupplierInvoices_SupplierInvoicesId",
                        column: x => x.SupplierInvoicesId,
                        principalTable: "SupplierInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentCustomerInvoices_DocumentsId",
                table: "DocumentCustomerInvoices",
                column: "DocumentsId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJournalEntries_JournalEntriesId",
                table: "DocumentJournalEntries",
                column: "JournalEntriesId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSupplierInvoices_SupplierInvoicesId",
                table: "DocumentSupplierInvoices",
                column: "SupplierInvoicesId");

            // Copy existing JournalEntryAttachment data to Document + DocumentData + join table
            // Step A: Add temp correlation column
            migrationBuilder.Sql(@"ALTER TABLE ""Documents"" ADD COLUMN _src_attachment_id int;");

            // Step B: Insert Documents with the correlation column
            migrationBuilder.Sql(@"
    INSERT INTO ""Documents"" (""OrganisationId"", ""FileName"", ""ContentType"", ""FileSize"", ""UploadedAt"", ""StorageKey"", ""SuggestedType"", ""ExtractedDataJson"", ""ClassifiedType"", _src_attachment_id)
    SELECT fy.""OrganisationId"", a.""FileName"", a.""ContentType"", a.""FileSize"", a.""UploadedAt"",
           '0', NULL, NULL, NULL, a.""Id""
    FROM ""JournalEntryAttachments"" a
    JOIN ""JournalEntries"" j ON j.""Id"" = a.""JournalEntryId""
    JOIN ""FiscalYears"" fy ON fy.""Id"" = j.""FiscalYearId"";
");

            // Step C: Update StorageKey to Document.Id
            migrationBuilder.Sql(@"
    UPDATE ""Documents"" SET ""StorageKey"" = CAST(""Id"" AS TEXT)
    WHERE ""StorageKey"" = '0';
");

            // Step D: Insert DocumentData using the correlation column
            migrationBuilder.Sql(@"
    INSERT INTO ""DocumentData"" (""DocumentId"", ""Data"")
    SELECT d.""Id"", a.""Data""
    FROM ""JournalEntryAttachments"" a
    JOIN ""Documents"" d ON d._src_attachment_id = a.""Id"";
");

            // Step E: Insert DocumentJournalEntries using the correlation column
            migrationBuilder.Sql(@"
    INSERT INTO ""DocumentJournalEntries"" (""DocumentsId"", ""JournalEntriesId"")
    SELECT d.""Id"", a.""JournalEntryId""
    FROM ""JournalEntryAttachments"" a
    JOIN ""Documents"" d ON d._src_attachment_id = a.""Id"";
");

            // Step F: Drop the temp column
            migrationBuilder.Sql(@"ALTER TABLE ""Documents"" DROP COLUMN _src_attachment_id;");

            migrationBuilder.DropTable(
                name: "JournalEntryAttachments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentCustomerInvoices");

            migrationBuilder.DropTable(
                name: "DocumentData");

            migrationBuilder.DropTable(
                name: "DocumentJournalEntries");

            migrationBuilder.DropTable(
                name: "DocumentSupplierInvoices");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.CreateTable(
                name: "JournalEntryAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalEntryAttachments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryAttachments_JournalEntryId",
                table: "JournalEntryAttachments",
                column: "JournalEntryId");
        }
    }
}
