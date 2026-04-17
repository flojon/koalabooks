# Decision: Migrate from SQLite to SQL Server with Aspire

**By:** Basher (Infra Dev)
**Date:** 2026-04-17
**Status:** Implemented

## Context

The project used SQLite everywhere for persistence. To align with production-grade infrastructure and Aspire orchestration, we migrated to SQL Server.

## Changes Made

### AppHost (`src/KoalaBooks.AppHost/AppHost.cs`)
- Added `Aspire.Hosting.SqlServer` package (13.2.2)
- AppHost now creates a SQL Server resource with `AddSqlServer("sql").AddDatabase("koalabooks")`
- Web project gets the connection via `.WithReference(sql).WaitFor(sql)`

### Infrastructure (`src/KoalaBooks.Infrastructure/`)
- Swapped `Microsoft.EntityFrameworkCore.Sqlite` → `Microsoft.EntityFrameworkCore.SqlServer` (10.0.6)
- `DesignTimeDbContextFactory` now uses `UseSqlServer` with a localdb connection string for EF tooling
- Deleted 4 old SQLite migrations, generated fresh `InitialCreate` for SQL Server

### Web (`src/KoalaBooks.Web/`)
- Swapped `Microsoft.EntityFrameworkCore.Sqlite` → `Aspire.Microsoft.EntityFrameworkCore.SqlServer` (13.2.2)
- `Program.cs` uses `builder.AddSqlServerDbContext<AppDbContext>("koalabooks")` instead of manual `UseSqlite`

### Tests — NOT touched
- `tests/KoalaBooks.Tests/` retains its own `Microsoft.EntityFrameworkCore.Sqlite` reference
- Tests use SQLite in-memory and are fully independent of the production provider

## Migration Generation

The `dotnet ef migrations add InitialCreate` command succeeded using the `DesignTimeDbContextFactory` which provides a direct SQL Server connection string (localdb), bypassing the Aspire runtime dependency. This is the correct pattern for EF tooling in Aspire projects.

## Verification

- `dotnet build` — 0 warnings, 0 errors
- `dotnet test` — 120/120 tests pass (SQLite in-memory, unaffected)
