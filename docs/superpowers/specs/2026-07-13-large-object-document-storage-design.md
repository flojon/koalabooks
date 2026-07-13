# Large Object Document Storage — Design Spec

**Date:** 2026-07-13
**Status:** Approved
**Issue:** [#214](https://github.com/flojon/koalabooks/issues/214)

## Overview

Follow-up to [#187](https://github.com/flojon/koalabooks/issues/187) / [#213](https://github.com/flojon/koalabooks/pull/213) (see [2026-07-09-stream-uploads-design.md](./2026-07-09-stream-uploads-design.md)), which moved `IDocumentStorage.SaveAsync` from `byte[]` to `Stream` at the interface level but left `DbDocumentStorage` buffering the full file into a `byte[]` before writing Postgres's `bytea` column. This spec removes that last buffer by migrating `DocumentData` storage from a `bytea` column to Postgres Large Objects (`NpgsqlLargeObjectManager`), with genuine chunked I/O for both writes and reads.

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

Postgres Large Objects require reads/writes to happen inside a transaction. Every method opens `db.Database.BeginTransactionAsync()` first, then drives a `NpgsqlLargeObjectManager` against the same underlying connection (`(NpgsqlConnection)db.Database.GetDbConnection()`) that EF Core's own queries in that method run on — so the LO operations and the `DocumentData` row change commit or roll back together atomically.

```csharp
public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<string> SaveAsync(int documentId, string contentType, Stream data)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var lom = new NpgsqlLargeObjectManager((NpgsqlConnection)db.Database.GetDbConnection());

        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
            await lom.UnlinkAsync(existing.Oid);

        var oid = await lom.CreateAsync(0);
        await using (var loStream = await lom.OpenReadWriteAsync(oid))
        {
            await data.CopyToAsync(loStream);
        }

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

        var lom = new NpgsqlLargeObjectManager((NpgsqlConnection)db.Database.GetDbConnection());
        await using var loStream = await lom.OpenReadAsync(row.Oid);
        using var ms = new MemoryStream();
        await loStream.CopyToAsync(ms);
        await tx.CommitAsync();
        return ms.ToArray();
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.DocumentData.FindAsync(id);
        if (row is null) return;

        var lom = new NpgsqlLargeObjectManager((NpgsqlConnection)db.Database.GetDbConnection());
        await lom.UnlinkAsync(row.Oid);
        db.DocumentData.Remove(row);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
```

`SaveAsync`'s `data.CopyToAsync(loStream)` is the actual fix: `NpgsqlLargeObjectManager`'s read/write stream does chunked I/O against the server, so no full-file `byte[]`/`MemoryStream` is ever built for the write path.

### Orphan-object safety

- **Save failure mid-write:** the whole operation is one transaction: if `CopyToAsync` throws or `SaveChangesAsync` fails, the transaction rolls back, and Postgres discards any Large Object created within that uncommitted transaction — no orphan.
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
    migrationBuilder.Sql("""UPDATE "DocumentData" SET "Oid" = lo_from_bytea(0, "Data")""");
    migrationBuilder.AlterColumn<uint>(name: "Oid", table: "DocumentData", type: "oid", nullable: false);
    migrationBuilder.DropColumn(name: "Data", table: "DocumentData");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<byte[]>(name: "Data", table: "DocumentData", type: "bytea", nullable: true);
    migrationBuilder.Sql("""UPDATE "DocumentData" SET "Data" = lo_get("Oid")""");
    migrationBuilder.AlterColumn<byte[]>(name: "Data", table: "DocumentData", type: "bytea", nullable: false);
    migrationBuilder.Sql("""SELECT lo_unlink("Oid") FROM "DocumentData\"""");
    migrationBuilder.DropColumn(name: "Oid", table: "DocumentData");
}
```

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
