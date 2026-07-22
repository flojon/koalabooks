using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace KoalaBooks.Tests;

// Covers the SieImportController endpoints added for issue #122 (Agent H stream, 5.H-1):
// synchronous preview + async import via SieImportJob/HangfireSieImportQueue, subsuming
// issue #279. The Testing environment wires ISieImportQueue to NoOpSieImportQueue (same
// pattern as ZipImportJob/NoOpZipImportQueue), so these tests verify the REST plumbing
// (enqueue → RunId → status-poll returns Pending) rather than the job's own execution,
// matching DocumentsApiTests' upload-zip coverage depth.
public class SieImportApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;

    private const string TestEmail = "api-test-sie-import@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    private const string SampleSie4 = """
        #FLAGGA 0
        #FORMAT PC8
        #SIETYP 4
        #PROGRAM "TestApp" 1.0
        #GEN 20260101
        #FNAMN "Koala AB"
        #ORGNR 5591234567
        #RAR 0 20260101 20261231
        #KONTO 1910 "Kassa"
        #KONTO 1930 "Foretagskonto"
        #KONTO 3010 "Forsaljning"
        #KONTO 5010 "Lokalhyra"
        #VER "A" 1 20260115 "Hyra januari"
        {
            #TRANS 5010 {} 10000.00 20260115 "Hyra"
            #TRANS 1930 {} -10000.00 20260115 "Hyra"
        }
        #VER "A" 2 20260201 "Kundbetalning"
        {
            #TRANS 1930 {} 25000.00 20260201 ""
            #TRANS 3010 {} -25000.00 20260201 ""
        }
        """;

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        _orgId = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<int> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (SIE Import)", Slug = "api-test-sie-import", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        return org.Id;
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

    private static byte[] SieBytes(string content = SampleSie4)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437).GetBytes(content);
    }

    private static MultipartFormDataContent BuildForm(byte[] bytes, string fileName = "export.se")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "File", fileName);
        return content;
    }

    // ── POST preview ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ValidSieFile_ReturnsPreview()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/sie/preview", BuildForm(SieBytes()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Koala AB", json.GetProperty("companyName").GetString());
        Assert.Equal(4, json.GetProperty("sieType").GetInt32());
        Assert.True(json.GetProperty("fiscalYears").GetArrayLength() >= 1);
        Assert.Equal(2, json.GetProperty("voucherCount").GetInt32());
    }

    [Fact]
    public async Task Preview_InvalidFile_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/sie/preview", BuildForm(Encoding.UTF8.GetBytes("not a sie file")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync("/api/v1/sie/preview", BuildForm(SieBytes()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST import / GET import status ───────────────────────────────────────

    [Fact]
    public async Task Import_ValidSieFile_ReturnsAcceptedWithRunId()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/sie/import", BuildForm(SieBytes()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("runId").GetInt32() > 0);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Import_InvalidFile_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/sie/import", BuildForm(Encoding.UTF8.GetBytes("not a sie file")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync("/api/v1/sie/import", BuildForm(SieBytes()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ImportStatus_ReturnsPendingRun()
    {
        var client = await AuthenticatedClientAsync();
        var importResponse = await client.PostAsync("/api/v1/sie/import", BuildForm(SieBytes()));
        var importJson = await importResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = importJson.GetProperty("runId").GetInt32();

        var response = await client.GetAsync($"/api/v1/sie/import/{runId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(runId, json.GetProperty("id").GetInt32());
        Assert.Equal("SieImport", json.GetProperty("jobType").GetString());
        Assert.Equal("Pending", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImportStatus_UnknownRunId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/sie/import/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImportStatus_CrossTenant_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var importResponse = await client.PostAsync("/api/v1/sie/import", BuildForm(SieBytes()));
        var importJson = await importResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = importJson.GetProperty("runId").GetInt32();

        // Second tenant, different user, same run id — must not see the first tenant's run.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var org2 = new Organisation { Name = "Other Org", Slug = "other-org-sie-import", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org2);
            await db.SaveChangesAsync();

            var user2 = new ApplicationUser
            {
                UserName = "other-" + TestEmail, Email = "other-" + TestEmail,
                EmailConfirmed = true, OrganisationId = org2.Id, DisplayName = "Other Tester"
            };
            var result = await userManager.CreateAsync(user2, TestPassword);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        var otherTokenResponse = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "koalabooks-api"),
            new KeyValuePair<string, string>("username", "other-" + TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        var otherToken = (await otherTokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        var otherClient = _factory.CreateClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);

        var response = await otherClient.GetAsync($"/api/v1/sie/import/{runId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
