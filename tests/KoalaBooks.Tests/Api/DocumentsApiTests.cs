using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace KoalaBooks.Tests;

// Covers the DocumentsController endpoints added for issue #122 (Agent F stream): pending
// list/count, upload, upload-zip + its status-poll endpoint, linked-documents list, link,
// update metadata, delete, download. Kept in its own file per the program plan's guidance
// (mirrors BankTransactionsApiTests.cs, the precedent for multipart uploads in this suite).
public class DocumentsApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;

    private const string TestEmail = "api-test-documents@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _fiscalYearId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Documents)", Slug = "api-test-documents", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        var fy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();

        return (org.Id, fy.Id);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-documents", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();

        return (org2.Id, fy2.Id);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<string> GetBearerTokenAsync()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "koalabooks-api"),
            new KeyValuePair<string, string>("username", TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var token = await GetBearerTokenAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MultipartFormDataContent BuildUploadForm(
        byte[] bytes, string fileName = "invoice.pdf", string contentType = "application/pdf")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "File", fileName);
        return content;
    }

    private static byte[] BuildZipBytes(string entryName = "invoice.pdf", byte[]? entryBytes = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            entryStream.Write(entryBytes ?? Encoding.UTF8.GetBytes("%PDF-1.4 fake pdf content"));
        }
        return ms.ToArray();
    }

    private async Task<int> SeedDocumentAsync(int orgId, string? classifiedType = null, DateOnly? documentDate = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = new Document
        {
            OrganisationId = orgId,
            FileName = "seeded.pdf",
            ContentType = "application/pdf",
            FileSize = 123,
            UploadedAt = DateTime.UtcNow,
            StorageKey = "",
            ClassifiedType = classifiedType,
            DocumentDate = documentDate
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private async Task<int> SeedJournalEntryAsync(int fiscalYearId, int entryNumber = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new JournalEntry
        {
            FiscalYearId = fiscalYearId, EntryNumber = entryNumber,
            Date = new DateOnly(2025, 6, 1), Description = "Seeded entry"
        };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task LinkDocumentToJournalEntryAsync(int documentId, int journalEntryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = await db.Documents.IgnoreQueryFilters().Include(d => d.JournalEntries)
            .FirstAsync(d => d.Id == documentId);
        var entry = await db.JournalEntries.IgnoreQueryFilters().FirstAsync(j => j.Id == journalEntryId);
        doc.JournalEntries.Add(entry);
        await db.SaveChangesAsync();
    }

    // ── GET pending ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Pending_ReturnsOnlyUnlinkedDocuments()
    {
        var unlinkedId = await SeedDocumentAsync(_orgId);
        var linkedId = await SeedDocumentAsync(_orgId);
        var entryId = await SeedJournalEntryAsync(_fiscalYearId);
        await LinkDocumentToJournalEntryAsync(linkedId, entryId);

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/documents/pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(unlinkedId, items);
        Assert.DoesNotContain(linkedId, items);
    }

    [Fact]
    public async Task Pending_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/documents/pending");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PendingCount_ReturnsCountOfUnlinkedDocuments()
    {
        await SeedDocumentAsync(_orgId);
        await SeedDocumentAsync(_orgId);

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/documents/pending-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PendingCount_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/documents/pending-count");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET linked ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Linked_ReturnsDocumentsLinkedToEntity()
    {
        var docId = await SeedDocumentAsync(_orgId);
        var entryId = await SeedJournalEntryAsync(_fiscalYearId);
        await LinkDocumentToJournalEntryAsync(docId, entryId);

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/documents/linked/JournalEntry/{entryId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(docId, items[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Linked_UnknownEntity_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/documents/linked/JournalEntry/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Linked_CrossTenantEntity_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var otherEntryId = await SeedJournalEntryAsync(otherFiscalYearId);
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/documents/linked/JournalEntry/{otherEntryId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Linked_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/documents/linked/JournalEntry/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST upload ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ReturnsCreatedDocument()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/documents", BuildUploadForm(Encoding.UTF8.GetBytes("%PDF-1.4 test")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invoice.pdf", json.GetProperty("fileName").GetString());
        Assert.True(json.GetProperty("id").GetInt32() > 0);
    }

    [Fact]
    public async Task Upload_DisallowedContentType_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync(
            "/api/v1/documents", BuildUploadForm(Encoding.UTF8.GetBytes("hello"), "notes.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync("/api/v1/documents", BuildUploadForm(Encoding.UTF8.GetBytes("%PDF-1.4 test")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST upload-zip / GET upload-zip status ─────────────────────────────

    [Fact]
    public async Task UploadZip_ReturnsAcceptedWithRunId()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync(
            "/api/v1/documents/upload-zip", BuildUploadForm(BuildZipBytes(), "inbox.zip", "application/zip"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("runId").GetInt32() > 0);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task UploadZip_InvalidZip_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync(
            "/api/v1/documents/upload-zip", BuildUploadForm(Encoding.UTF8.GetBytes("not a zip"), "inbox.zip", "application/zip"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadZip_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync(
            "/api/v1/documents/upload-zip", BuildUploadForm(BuildZipBytes(), "inbox.zip", "application/zip"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ZipImportStatus_ReturnsRun()
    {
        var client = await AuthenticatedClientAsync();
        var uploadResponse = await client.PostAsync(
            "/api/v1/documents/upload-zip", BuildUploadForm(BuildZipBytes(), "inbox.zip", "application/zip"));
        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = uploadJson.GetProperty("runId").GetInt32();

        var response = await client.GetAsync($"/api/v1/documents/upload-zip/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(runId, json.GetProperty("id").GetInt32());
        Assert.Equal("ZipImport", json.GetProperty("jobType").GetString());
    }

    [Fact]
    public async Task ZipImportStatus_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/documents/upload-zip/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ZipImportStatus_CrossTenant_Returns404()
    {
        var (otherOrgId, _) = await SeedSecondTenantAsync();
        int otherRunId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = new BackgroundJobRun { OrganisationId = otherOrgId, JobType = BackgroundJobType.ZipImport };
            db.BackgroundJobRuns.Add(run);
            await db.SaveChangesAsync();
            otherRunId = run.Id;
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/documents/upload-zip/{otherRunId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ZipImportStatus_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/documents/upload-zip/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST link ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Link_LinksDocumentToJournalEntry()
    {
        var docId = await SeedDocumentAsync(_orgId);
        var entryId = await SeedJournalEntryAsync(_fiscalYearId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/documents/{docId}/link",
            new { entityType = "JournalEntry", entityId = entryId });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var linkedResponse = await client.GetAsync($"/api/v1/documents/linked/JournalEntry/{entryId}");
        var json = await linkedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(json.EnumerateArray());
    }

    [Fact]
    public async Task Link_UnknownDocument_Returns404()
    {
        var entryId = await SeedJournalEntryAsync(_fiscalYearId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/documents/999999/link",
            new { entityType = "JournalEntry", entityId = entryId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Link_CrossTenantDocument_Returns404()
    {
        var (otherOrgId, _) = await SeedSecondTenantAsync();
        var otherDocId = await SeedDocumentAsync(otherOrgId);
        var entryId = await SeedJournalEntryAsync(_fiscalYearId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/documents/{otherDocId}/link",
            new { entityType = "JournalEntry", entityId = entryId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Link_UnknownEntity_Returns404()
    {
        var docId = await SeedDocumentAsync(_orgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/documents/{docId}/link",
            new { entityType = "JournalEntry", entityId = 999999 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Link_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/documents/1/link",
            new { entityType = "JournalEntry", entityId = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── PUT (update metadata) ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMetadata_UpdatesClassifiedTypeAndDate()
    {
        var docId = await SeedDocumentAsync(_orgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/documents/{docId}",
            new { classifiedType = "supplier-invoice", documentDate = "2025-06-01" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doc = await db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
        Assert.Equal("supplier-invoice", doc.ClassifiedType);
        Assert.Equal(new DateOnly(2025, 6, 1), doc.DocumentDate);
    }

    [Fact]
    public async Task UpdateMetadata_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/documents/999999",
            new { classifiedType = "supplier-invoice" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateMetadata_CrossTenant_Returns404()
    {
        var (otherOrgId, _) = await SeedSecondTenantAsync();
        var otherDocId = await SeedDocumentAsync(otherOrgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/documents/{otherDocId}",
            new { classifiedType = "supplier-invoice" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateMetadata_WithoutToken_Returns401()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/documents/1", new { classifiedType = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        var docId = await SeedDocumentAsync(_orgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillThere = await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == docId);
        Assert.False(stillThere);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync("/api/v1/documents/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_CrossTenant_Returns404()
    {
        var (otherOrgId, _) = await SeedSecondTenantAsync();
        var otherDocId = await SeedDocumentAsync(otherOrgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/v1/documents/{otherDocId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutToken_Returns401()
    {
        var response = await _client.DeleteAsync("/api/v1/documents/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET download ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReturnsUploadedFileBytes()
    {
        var client = await AuthenticatedClientAsync();
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 downloadable content");
        var uploadResponse = await client.PostAsync("/api/v1/documents", BuildUploadForm(bytes));
        var uploadJson = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var docId = uploadJson.GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/v1/documents/{docId}/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, downloaded);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Download_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/documents/999999/download");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_CrossTenant_Returns404()
    {
        var (otherOrgId, _) = await SeedSecondTenantAsync();
        var otherDocId = await SeedDocumentAsync(otherOrgId);
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/documents/{otherDocId}/download");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/documents/1/download");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
