# Large Object Document Storage — Design Spec

**Date:** 2026-07-13
**Status:** Approved
**Issue:** [#214](https://github.com/flojon/koalabooks/issues/214)

## Overview

Follow-up to [#187](https://github.com/flojon/koalabooks/issues/187) / [#213](https://github.com/flojon/koalabooks/pull/213) (see [2026-07-09-stream-uploads-design.md](./2026-07-09-stream-uploads-design.md)), which moved `IDocumentStorage.SaveAsync` from `byte[]` to `Stream` at the interface level but left `DbDocumentStorage` buffering the full file into a `byte[]` before writing Postgres's `bytea` column. This spec removes that last buffer by migrating `DocumentData` storage from a `bytea` column to Postgres Large Objects, with genuine chunked I/O for both writes and reads. (See "API choice" below on `NpgsqlLargeObjectManager` vs. the raw `lo_*` functions.)

## Goals / Non-Goals

- **Goal:** `DbDocumentStorage.SaveAsync` writes the incoming `Stream` to Postgres without ever materializing the whole file in memory.
- **Goal:** `LoadAsync`/`DeleteAsync` move to the Large Object API too, since the underlying storage mechanism changes for all of them, not just writes.
- **Goal:** one-shot data migration of existing `DocumentData` rows from `bytea` to Large Objects — no permanent mixed-storage mode.
- **Non-goal:** any change to `IDocumentStorage`'s public signature. `SaveAsync(int, string, Stream)` is unchanged; `LoadAsync` keeps returning `Task<byte[]>` since every current caller (`IDocumentExtractor`, download endpoints) needs materialized bytes regardless of how the storage layer itself streams.
- **Non-goal:** streaming PDF extraction. `IDocumentExtractor`/PdfPig stays `byte[]`-based, fed from `DocumentService`'s own `ReadBoundedAsync` buffer — independent of how `DbDocumentStorage` persists bytes.
- **Non-goal:** changes to `UploadZipAsync`'s internal zip handling — unaffected, it already produces per-entry `byte[]`s independently.

## Entity change

`DocumentData.cs`: replace `byte[] Data` with `uint Oid`, the Large Object's identifier.

```csharp
public class DocumentData
{
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public uint Oid { get; set; }
}
```

`AppDbContext.OnModelCreating` (inline block, same style as today — no `IEntityTypeConfiguration` classes in this project):

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

`.HasColumnType("oid")` is explicit rather than relying on convention, since a bare `uint` property would otherwise map to `integer`.

## `DbDocumentStorage`

Postgres Large Objects require reads/writes to happen inside a transaction. Every method opens `db.Database.BeginTransactionAsync()` first, then issues raw SQL against the same underlying connection (`(NpgsqlConnection)db.Database.GetDbConnection()`) that EF Core's own queries in that method run on — so the LO operations and the `DocumentData` row change commit or roll back together atomically.

A retry-strategy incompatibility was discovered during implementation: the production app enables `NpgsqlRetryingExecutionStrategy` (via `EnrichNpgsqlDbContext`), which forbids opening a transaction directly — it must be started from inside `db.Database.CreateExecutionStrategy().ExecuteAsync(...)` so a transient failure can retry the whole operation. `SaveAsync` and `DeleteAsync` were fixed to wrap their bodies in `strategy.ExecuteAsync(async () => { ... })`, with a scoped `DetachTrackedDocumentData(documentId)` helper called both at the start of each attempt and in a `catch`/rethrow around the transactional body, so a prior failed attempt (retried or terminally failed) never leaves a stray tracked `DocumentData` entity on the shared, caller-owned `AppDbContext`. See `.superpowers/sdd/task-1-fix-report.md` for the full detail. The code blocks below show the resulting shape; see `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs` for the authoritative, current version.

**API choice:** the issue names `NpgsqlLargeObjectManager`, but that class is marked `[Obsolete]` in the installed Npgsql version (10.0.3) — it still compiles (a CS0618 warning, and this repo has no `TreatWarningsAsErrors`), but Npgsql's own guidance is to call the underlying `lo_*` server-side functions directly instead. Verified by compiling both approaches against a scratch project: confirmed obsolete but functional, then confirmed the raw-SQL approach compiles warning-free. Decision: use the raw SQL functions (`lo_create`, `lo_open`, `loread`, `lowrite`, `lo_close`, `lo_unlink`) directly via `NpgsqlCommand`, matching current Npgsql guidance despite the extra code this requires over the wrapper class.

```csharp
public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    // https://www.postgresql.org/docs/current/lo-interfaces.html#LO-INTERFACES-OPEN
    private const int InvWrite = 0x00020000;
    private const int InvRead = 0x00040000;
    private const int ChunkSize = 81920; // matches Stream.CopyToAsync's default buffer size

    public async Task<string> SaveAsync(int documentId, string contentType, Stream data)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole delegate: a prior failed attempt may
            // have left a DocumentData row tracked (Added/Modified) without
            // committing — detach just that row before re-reading it, and
            // rewind the input (when possible). db is a shared, caller-owned
            // AppDbContext, so this must not touch entities outside our own.
            DetachTrackedDocumentData(documentId);
            if (data.CanSeek) data.Position = 0;

            try
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
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state.
                DetachTrackedDocumentData(documentId);
                throw;
            }
        });
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
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
        });
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            DetachTrackedDocumentData(id);

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = await db.DocumentData.FindAsync(id);
                if (row is null) return;

                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, row.Oid));
                db.DocumentData.Remove(row);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state.
                DetachTrackedDocumentData(id);
                throw;
            }
        });
    }

    // Detaches only a stale DocumentData entry left tracked by a previous,
    // retried attempt of this same call — never touches unrelated entities
    // tracked by the caller on this shared AppDbContext.
    private void DetachTrackedDocumentData(int documentId)
    {
        var entry = db.ChangeTracker.Entries<DocumentData>()
            .FirstOrDefault(e => e.Entity.DocumentId == documentId);
        if (entry is not null) entry.State = EntityState.Detached;
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

`SaveAsync`'s read/`lowrite` loop is the actual fix: each chunk read from `data` is written to the LO via its own `lowrite` call, so no full-file `byte[]`/`MemoryStream` is ever built for the write path. `LoadAsync`'s `loread` loop is the read-side counterpart — it still assembles a `byte[]` at the end (interface constraint, see Goals), but reads it from Postgres in bounded chunks rather than one unbounded transfer.

### Orphan-object safety

- **Save failure mid-write:** the whole operation is one transaction: if a `lowrite` call throws or `SaveChangesAsync` fails, the transaction rolls back, and Postgres discards any Large Object created within that uncommitted transaction — no orphan.
- **Save overwriting an existing document:** the old `Oid` is unlinked *before* the new one is created, inside the same transaction as the entity update, so a rollback restores the original LO rather than leaving two.
- **Delete:** already unlinks before removing the row, matching `DocumentService.DeleteAsync`'s existing correct order (line 160-161: `storage.DeleteAsync(doc.StorageKey)` before `db.Documents.Remove(doc)`).
- **Upload-failure rollback in `DocumentService.UploadAsync`** (line 53-58: `catch` removes the just-created `Document` row without calling `storage.DeleteAsync`): this only runs when `storage.SaveAsync` itself threw, i.e. by definition no LO was ever committed — no change needed here.
- Cascading deletes of `Document` that bypass `DocumentService` entirely would still orphan an LO (Postgres FK cascades don't know about Large Objects), but no such code path exists today (verified: only two call sites remove `Document` rows, both handled above). Not addressed further — YAGNI.

## Migration

One EF Core migration, following the existing raw-SQL-data-migration precedent (`20260601184322_DocumentInbox.cs`):

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<uint>(name: "Oid", table: "DocumentData", type: "oid", nullable: true);
    migrationBuilder.Sql(@"UPDATE ""DocumentData"" SET ""Oid"" = lo_from_bytea(0, ""Data"");");
    migrationBuilder.AlterColumn<uint>(name: "Oid", table: "DocumentData", type: "oid", nullable: false,
        oldClrType: typeof(uint), oldType: "oid", oldNullable: true);
    migrationBuilder.DropColumn(name: "Data", table: "DocumentData");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<byte[]>(name: "Data", table: "DocumentData", type: "bytea", nullable: true);
    migrationBuilder.Sql(@"UPDATE ""DocumentData"" SET ""Data"" = lo_get(""Oid"");");
    migrationBuilder.AlterColumn<byte[]>(name: "Data", table: "DocumentData", type: "bytea", nullable: false,
        oldClrType: typeof(byte[]), oldType: "bytea", oldNullable: true);
    migrationBuilder.Sql(@"SELECT lo_unlink(""Oid"") FROM ""DocumentData"";");
    migrationBuilder.DropColumn(name: "Oid", table: "DocumentData");
}
```

(Verified: this exact Up/Down SQL was run against a real `postgres:17-alpine` container with sample rows — including a normal value, a single zero byte, and an empty `bytea` — and round-tripped correctly in both directions, including `lo_unlink` cleanup on rollback.)

`lo_from_bytea(loid, data)` and `lo_get(loid)` are built-in Postgres server-side functions (since 9.4) — no `lo` contrib extension needed.

Production applies this automatically via the existing `db.Database.MigrateAsync()` startup path (`Program.cs`, retried up to 10x). The `"Testing"` environment uses `db.Database.EnsureCreated()`, which builds schema straight from the EF model and never runs migrations — so correctness depends on the `OnModelCreating` change above, not the migration file, for tests to see the right schema. This matches the existing test-environment behavior; no change needed there.

## Testing

- Existing `DbDocumentStorageTests` (`SaveAsync_AcceptsStreamAndRoundTripsThroughLoadAsync`, `SaveAsync_OverwritesExistingDataOnReupload`) continue to pass unchanged — they exercise round-trip/overwrite behavior against whatever `IDocumentStorage` does internally, which is exactly what needs to keep working.
- New test: save via a forward-only, non-`MemoryStream` `Stream` wrapper (e.g. wrapping a `MemoryStream` in a minimal pass-through `Stream` subclass that throws if `Length`/`Position` is accessed) to guard against a full-buffer implementation creeping back in.
- New test: delete unlinks the Large Object — save a document, capture its `Oid` via a raw query, delete it, assert `SELECT lo_get(oid)` (or catching the resulting exception) confirms the LO no longer exists.
- Migration correctness verified manually (or via a small integration test that seeds a pre-migration-shaped row and asserts data survives) since Testcontainers-based tests don't run migrations (`EnsureCreated` path) — call out in the plan that this needs an explicit manual check against a database that actually runs `MigrateAsync`.

## What Is Not Changing

- `IDocumentStorage`'s public interface — unchanged signatures.
- `IDocumentExtractor`/`PdfTextExtractor` — unchanged, still `byte[]`-based.
- `UploadZipAsync`'s internal zip-entry handling — unchanged.
- `DocumentService` — no changes; it only calls through `IDocumentStorage`.
