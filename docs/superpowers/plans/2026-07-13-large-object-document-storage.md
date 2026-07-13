# Large Object Document Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `DbDocumentStorage.SaveAsync` write uploaded files to Postgres without ever materializing the whole file in memory, by migrating `DocumentData` storage from a `bytea` column to Postgres Large Objects.

**Architecture:** `DocumentData.Data` (`byte[]`/`bytea`) becomes `DocumentData.Oid` (`uint`/`oid`). `DbDocumentStorage` drives the Large Object lifecycle (`lo_create`, `lo_open`, `loread`/`lowrite`, `lo_close`, `lo_unlink`) via raw `NpgsqlCommand`s issued on the same connection/transaction EF Core's own `DocumentData` row updates run on, so LO changes and row changes commit or roll back together. A one-shot EF Core migration converts existing rows using Postgres's built-in `lo_from_bytea`/`lo_get` functions, then drops the `bytea` column.

**Tech Stack:** .NET 10 / EF Core 10 / `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.2 (base `Npgsql` driver 10.0.3, added as a direct package reference) / xUnit + Testcontainers.PostgreSql (`postgres:17-alpine`).

## Global Constraints

- `IDocumentStorage`'s public interface does not change: `SaveAsync(int, string, Stream)` returns `Task<string>`; `LoadAsync(string)` returns `Task<byte[]>`; `DeleteAsync(string)` returns `Task`.
- `NpgsqlLargeObjectManager` must NOT be used — it is `[Obsolete]` in the installed Npgsql version. Use the raw `lo_*` SQL functions via `NpgsqlCommand` instead (verified to compile warning-free and to correctly participate in an ambient EF Core transaction without needing an explicit `.Transaction` assignment on the command).
- Every Large Object read/write/unlink happens inside a transaction opened via `db.Database.BeginTransactionAsync()`, using `(NpgsqlConnection)db.Database.GetDbConnection()` as the connection for all raw commands in that method. This transaction must be opened from inside `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, not directly — the production configuration enables `NpgsqlRetryingExecutionStrategy` (via `EnrichNpgsqlDbContext`), which forbids starting a transaction outside of `ExecuteAsync`'s retry delegate. This was discovered during implementation; see `.superpowers/sdd/task-1-fix-report.md` and the design spec's `DbDocumentStorage` section for the fixed shape.
- `IDocumentExtractor`, `UploadZipAsync`, and `DocumentService` are out of scope — no changes to any of them.
- The `"Testing"` ASP.NET environment builds schema via `db.Database.EnsureCreated()` (from the EF model), not via migrations — so the `DocumentData` entity/`OnModelCreating` change is what test correctness depends on, not the migration file.

---

### Task 1: Migrate `DocumentData` storage to Postgres Large Objects

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`
- Modify: `src/KoalaBooks.Domain/Entities/DocumentData.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs:277-284`
- Modify: `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- Test: `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs`

**Interfaces:**
- Produces: `DocumentData.Oid` (`uint`, EF-mapped to Postgres `oid`), replacing `DocumentData.Data` (`byte[]`). Task 2 (the migration) depends on this exact property name and mapping.
- Consumes: nothing from other tasks — this task is self-contained and is the prerequisite for Task 2.

- [ ] **Step 1: Add a direct `Npgsql` package reference**

`DbDocumentStorage.cs` will use `NpgsqlConnection`, `NpgsqlCommand`, `NpgsqlParameter`, and `NpgsqlDbType` directly. These currently resolve only transitively through `Npgsql.EntityFrameworkCore.PostgreSQL`; add an explicit reference pinned to the resolved transitive version (10.0.3) so the dependency is direct rather than implicit.

In `src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`, in the existing `<ItemGroup>` containing `Npgsql.EntityFrameworkCore.PostgreSQL`, add:

```xml
    <PackageReference Include="Npgsql" Version="10.0.3" />
```

placed alphabetically, immediately before the `Npgsql.EntityFrameworkCore.PostgreSQL` line.

- [ ] **Step 2: Update the `DocumentData` entity**

Replace the full contents of `src/KoalaBooks.Domain/Entities/DocumentData.cs`:

```csharp
namespace KoalaBooks.Domain.Entities;

public class DocumentData
{
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public uint Oid { get; set; }
}
```

- [ ] **Step 3: Update the EF Core mapping**

In `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, replace lines 277-284:

```csharp
        modelBuilder.Entity<DocumentData>(entity =>
        {
            entity.HasKey(d => d.DocumentId);
            entity.HasOne(d => d.Document)
                  .WithOne()
                  .HasForeignKey<DocumentData>(d => d.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
```

with:

```csharp
        modelBuilder.Entity<DocumentData>(entity =>
        {
            entity.HasKey(d => d.DocumentId);
            entity.Property(d => d.Oid).HasColumnType("oid");
            entity.HasOne(d => d.Document)
                  .WithOne()
                  .HasForeignKey<DocumentData>(d => d.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Write the new failing tests**

Replace the full contents of `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs` (this adds two new tests to the existing two; the existing two are unchanged in behavior but must still compile against the new `DbDocumentStorage`):

```csharp
using System.Data;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Tests;

public class DbDocumentStorageTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task SaveAsync_AcceptsStreamAndRoundTripsThroughLoadAsync()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 3,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var bytes = new byte[] { 1, 2, 3 };
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream(bytes));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingDataOnReupload()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([1]));
        await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([9, 9]));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(new byte[] { 9, 9 }, loaded);
    }

    [Fact]
    public async Task SaveAsync_WorksWithForwardOnlyNonSeekableStream()
    {
        // Guards against reintroducing type-special-casing (e.g. the old
        // `data is MemoryStream alreadyBuffered` branch) that assumes a
        // concrete, seekable stream type instead of reading generically.
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 5,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var bytes = new byte[] { 5, 4, 3, 2, 1 };
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new ForwardOnlyStream(new MemoryStream(bytes)));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task DeleteAsync_UnlinksTheUnderlyingLargeObject()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 2,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([7, 8]));
        var row = await _fx.Db.DocumentData.FindAsync(doc.Id);
        var oid = row!.Oid;

        await storage.DeleteAsync(key);

        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT lo_get(@oid)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet build tests/KoalaBooks.Tests`
Expected: BUILD FAILS. `DbDocumentStorage.cs` still references the now-removed `DocumentData.Data` property (`existing.Data = bytes`, `Data = bytes`, `row?.Data`), and `AppDbContextModelSnapshot.cs`/other callers are unaffected but `DbDocumentStorage.cs` itself won't compile. This confirms the new entity shape is in place and the implementation hasn't been updated yet.

- [ ] **Step 6: Implement `DbDocumentStorage` using raw Large Object SQL functions**

Replace the full contents of `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`:

```csharp
// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public async Task<string> SaveAsync(int documentId, string contentType, Stream data)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
            await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, existing.Oid));

        var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await data.ReadAsync(buffer)) > 0)
        {
            var chunk = buffer[..read];
            await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

        if (existing is not null)
            existing.Oid = oid;
        else
            db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return documentId.ToString();
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.DocumentData.FindAsync(id);
        if (row is null) return [];

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
            ("oid", NpgsqlDbType.Oid, row.Oid), ("mode", NpgsqlDbType.Integer, InvRead));

        using var ms = new MemoryStream();
        while (true)
        {
            var chunk = await ExecuteScalarAsync<byte[]>(conn, "SELECT loread(@fd, @len)",
                ("fd", NpgsqlDbType.Integer, fd), ("len", NpgsqlDbType.Integer, ChunkSize));
            if (chunk.Length > 0) await ms.WriteAsync(chunk);
            if (chunk.Length < ChunkSize) break;
        }
        await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));
        await tx.CommitAsync();
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.DocumentData.FindAsync(id);
        if (row is null) return;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, row.Oid));
        db.DocumentData.Remove(row);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(NpgsqlConnection conn, string sql,
        params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, type, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = name, NpgsqlDbType = type, Value = value });
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }
}
```

Note: raw `NpgsqlCommand`s created on `conn` without an explicit `.Transaction` assignment correctly participate in and roll back with the connection's currently active ambient transaction (verified directly against a real Postgres container: a Large Object created via such a command was confirmed gone after `RollbackAsync()`). No `NpgsqlTransaction` needs to be threaded through `ExecuteScalarAsync`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~DbDocumentStorageTests`
Expected: PASS (4 tests).

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj \
        src/KoalaBooks.Domain/Entities/DocumentData.cs \
        src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs \
        tests/KoalaBooks.Tests/DbDocumentStorageTests.cs
git commit -m "Migrate DbDocumentStorage from bytea to Postgres Large Objects

Removes the last full-file byte[] buffer in DbDocumentStorage.SaveAsync
by streaming chunked writes to a Large Object via lo_create/lo_open/
lowrite/lo_close, driven through raw NpgsqlCommands on the same
transaction as the DocumentData row update. NpgsqlLargeObjectManager
is avoided since it is obsolete in the installed Npgsql version."
```

---

### Task 2: Add the EF Core migration and verify data-migrating SQL

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_DocumentDataLargeObjects.cs`
- Create: `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_DocumentDataLargeObjects.Designer.cs` (generated, not hand-edited)
- Modify: `src/KoalaBooks.Infrastructure/Migrations/AppDbContextModelSnapshot.cs` (generated, not hand-edited)
- Test: `tests/KoalaBooks.Tests/DocumentDataMigrationSqlTests.cs`

**Interfaces:**
- Consumes: `DocumentData.Oid` (`uint`, `HasColumnType("oid")`) from Task 1 — the migration's target schema must match this exactly, since `dotnet ef migrations add` diffs against the current model.
- Produces: nothing consumed by later tasks (Task 3 is a manual smoke test only).

- [ ] **Step 1: Generate the migration scaffold**

Run:
```bash
dotnet ef migrations add DocumentDataLargeObjects \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear in `src/KoalaBooks.Infrastructure/Migrations/` named `<timestamp>_DocumentDataLargeObjects.cs` and `.Designer.cs`; `AppDbContextModelSnapshot.cs` is updated to reflect `DocumentData.Oid` (`oid`, not nullable) replacing `DocumentData.Data` (`bytea`). The generated `Up()`/`Down()` bodies will likely just do `DropColumn("Data")` + `AddColumn<uint>("Oid", ...)` with no data-preserving step — that's expected, it gets replaced in the next step. Do not hand-edit `.Designer.cs` or `AppDbContextModelSnapshot.cs`; only the migration class itself needs rewriting.

- [ ] **Step 2: Replace the generated Up/Down with the verified data-migrating SQL**

Open the newly generated `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_DocumentDataLargeObjects.cs` and replace its `Up`/`Down` method bodies (keep the generated class name, namespace, and `#nullable disable`/using directives from the scaffold) with:

```csharp
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
```

This exact Up/Down SQL was already verified against a real `postgres:17-alpine` container with three sample rows (a normal multi-byte value, a single zero byte, and an empty `bytea`) — all round-tripped correctly through `lo_from_bytea`/`lo_get` in both directions, and `lo_unlink` correctly removed the Large Objects on rollback.

- [ ] **Step 3: Write a test that directly exercises the migration's SQL functions**

Testcontainers-based tests never run EF migrations (the `"Testing"` environment uses `EnsureCreated()`), so add a test that verifies `lo_from_bytea`/`lo_get` — the two Postgres functions the migration's `Up()`/`Down()` rely on — behave correctly against the same Postgres version (`postgres:17-alpine`) the rest of the test suite uses.

Create `tests/KoalaBooks.Tests/DocumentDataMigrationSqlTests.cs`:

```csharp
using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace KoalaBooks.Tests;

public class DocumentDataMigrationSqlTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task LoFromBytea_ThenLoGet_RoundTripsExactBytes()
    {
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        var original = new byte[] { 10, 20, 30, 40, 250 };

        await using var tx = await conn.BeginTransactionAsync();

        uint oid;
        await using (var createCmd = new NpgsqlCommand(@"SELECT lo_from_bytea(0, @data)", conn))
        {
            createCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "data", NpgsqlDbType = NpgsqlDbType.Bytea, Value = original });
            oid = (uint)(await createCmd.ExecuteScalarAsync())!;
        }

        byte[] roundTripped;
        await using (var readCmd = new NpgsqlCommand(@"SELECT lo_get(@oid)", conn))
        {
            readCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            roundTripped = (byte[])(await readCmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public async Task LoFromBytea_ThenLoGet_RoundTripsEmptyBytea()
    {
        var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();

        uint oid;
        await using (var createCmd = new NpgsqlCommand(@"SELECT lo_from_bytea(0, @data)", conn))
        {
            createCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "data", NpgsqlDbType = NpgsqlDbType.Bytea, Value = Array.Empty<byte>() });
            oid = (uint)(await createCmd.ExecuteScalarAsync())!;
        }

        byte[] roundTripped;
        await using (var readCmd = new NpgsqlCommand(@"SELECT lo_get(@oid)", conn))
        {
            readCmd.Parameters.Add(new NpgsqlParameter { ParameterName = "oid", NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            roundTripped = (byte[])(await readCmd.ExecuteScalarAsync())!;
        }

        await tx.CommitAsync();

        Assert.Empty(roundTripped);
    }
}
```

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~DocumentDataMigrationSqlTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Verify the migration applies cleanly against a real Postgres**

If a local Postgres is available, run:
```bash
dotnet ef database update \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web \
  --connection "Host=localhost;Database=koalabooks_migration_check;Username=postgres;Password=postgres"
```
Expected: applies cleanly, ending on `DocumentDataLargeObjects`. If no local Postgres is available, skip this — Step 4's test already verified the exact SQL functions the migration uses, against the same Postgres version as production (`postgres:17-alpine`).

- [ ] **Step 6: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, all tests (no regressions in unrelated suites).

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Migrations/ tests/KoalaBooks.Tests/DocumentDataMigrationSqlTests.cs
git commit -m "Add EF Core migration: DocumentData bytea column to Large Objects

One-shot data migration converts existing DocumentData.Data (bytea)
rows to Postgres Large Objects via lo_from_bytea, then drops the bytea
column. Down() reverses via lo_get and unlinks the Large Objects."
```

---

### Task 3: Manual verification against the running app

**Files:** none (verification only).

**Interfaces:** none — this task only exercises the app end-to-end.

- [ ] **Step 1: Start the app**

Use the project's `run` skill (or `aspire start` per the `aspire` skill) to launch KoalaBooks locally against a real Postgres.

- [ ] **Step 2: Upload a document**

In the browser, go to the Inbox (or any of the four upload call sites — Inbox, CustomerInvoices, SupplierInvoices, Journal) and upload a PDF or image file.

Expected: upload succeeds, no error shown.

- [ ] **Step 3: Load/view the uploaded document**

Open or download the just-uploaded document from the UI.

Expected: the downloaded/viewed file is byte-identical to what was uploaded (visually verify a PDF renders correctly, or an image displays correctly).

- [ ] **Step 4: Delete the document**

Delete the uploaded document via the UI.

Expected: delete succeeds with no error. (Optionally, if you have DB access, confirm no `pg_largeobject` entries remain for that document's former `Oid` — not required, since `DeleteAsync_UnlinksTheUnderlyingLargeObject` in Task 1 already covers this at the unit level.)

- [ ] **Step 5: Re-upload to the same document slot**

Upload a different file over an existing `Document` (e.g. via the same journal-entry attachment flow used for overwrite), to exercise the `SaveAsync` overwrite path (old LO unlinked, new one created) against the running app, not just the unit test.

Expected: the newly loaded file reflects the new content; no error; no leftover behavior indicating the old Large Object wasn't cleaned up.
