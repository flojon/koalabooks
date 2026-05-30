# REST API Design — KoalaBooks Public API (v1)

**Date:** 2026-05-30  
**Issue:** #11  
**Status:** Approved

## Overview

Add a public REST API to KoalaBooks so third-party tools, mobile apps, and automation scripts can read and write accounting data without going through the Blazor UI. V1 covers the three core accounting resources: fiscal years, accounts, and journal entries.

## Scope

**In scope (v1):**
- Fiscal years — read only
- Accounts — read only (nested under fiscal years)
- Journal entries — read + create + delete

**Out of scope for v1:** supplier invoices, bank transactions, customers, customer invoices, account writes, fiscal year creation, bulk operations.

## Architecture

The API lives inside the existing `KoalaBooks.Web` project alongside the Blazor app. No new project is created. Controllers and minimal API routes coexist; the existing OpenIddict auth setup, TenantContext, and AppDbContext global query filters all work unchanged.

### New files

```
src/KoalaBooks.Web/
  Controllers/
    Api/
      FiscalYearsController.cs
      AccountsController.cs
      JournalEntriesController.cs
  Models/
    Api/
      FiscalYearResponse.cs
      AccountResponse.cs
      JournalEntryResponse.cs
      JournalEntryLineResponse.cs
      CreateJournalEntryRequest.cs
      CreateJournalEntryLineRequest.cs
      PagedResult.cs
```

### Program.cs additions

```csharp
builder.Services.AddControllers();
builder.Services.AddOpenApi();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
    app.MapScalarApiReference();
app.MapControllers();
```

## Authentication & Authorization

OpenIddict is already configured with password flow, refresh token flow, and authorization code flow. Clients obtain a bearer token via `POST /connect/token` with `grant_type=password`. All API controllers carry `[Authorize]`, which uses the existing `AddValidation().UseLocalServer().UseAspNetCore()` validation pipeline.

Tenant scoping is automatic: `TenantContext` reads `org_id` from the bearer token's claims, and the global query filters on `AppDbContext` scope all queries to that organisation. No additional auth work is required.

No new rate limiting policy is added in v1. The existing `auth` policy covers `/connect/token`.

## Endpoints

All routes are prefixed `/api/v1/`.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/v1/fiscal-years` | List all fiscal years (tenant-scoped) |
| GET | `/api/v1/fiscal-years/{id}` | Get a single fiscal year |
| GET | `/api/v1/fiscal-years/{fiscalYearId}/accounts` | List accounts for a fiscal year |
| GET | `/api/v1/accounts/{id}` | Get a single account |
| GET | `/api/v1/fiscal-years/{fiscalYearId}/journal-entries` | List entries — paginated, filterable by date |
| GET | `/api/v1/journal-entries/{id}` | Get a single journal entry with lines |
| POST | `/api/v1/fiscal-years/{fiscalYearId}/journal-entries` | Create a journal entry |
| DELETE | `/api/v1/journal-entries/{id}` | Delete a journal entry |

### Pagination

Journal-entries list supports offset-based pagination: `?page=1&pageSize=50`. Maximum `pageSize` is 200. Response uses `PagedResult<T>`.

### Filtering

Journal-entries list supports date range filtering: `?from=2024-01-01&to=2024-12-31`. Maps directly to the existing `GetByFiscalYearAsync(fiscalYearId, from, to)` parameters.

## Request & Response Models

### Responses

```csharp
FiscalYearResponse    { int Id, string Name, DateOnly StartDate, DateOnly EndDate, bool IsClosed }
AccountResponse       { int Id, string AccountNumber, string Name, string AccountClass,  // enum serialised as string
                        bool IsActive, decimal IncomingBalance, decimal OutgoingBalance }
JournalEntryResponse  { int Id, int EntryNumber, DateOnly Date, string Description,
                        bool IsPosted, DateTime CreatedAt, List<JournalEntryLineResponse> Lines }
JournalEntryLineResponse { int Id, int AccountId, string AccountNumber, string AccountName,
                           decimal DebitAmount, decimal CreditAmount }
PagedResult<T>        { List<T> Items, int Page, int PageSize, int TotalCount }
```

Domain entities are never serialised directly — controllers always map to response models.

### Create request

```csharp
CreateJournalEntryRequest      { DateOnly Date, string Description,
                                  List<CreateJournalEntryLineRequest> Lines }
CreateJournalEntryLineRequest  { int AccountId, decimal DebitAmount, decimal CreditAmount }
```

The request maps to `JournalEntry` + `JournalEntryLine` entities before being passed to the existing `JournalEntryService.CreateAsync`. Business validation (balanced debits/credits, accounts belonging to the fiscal year, fiscal year open) is handled by the service layer.

## Error Handling

| Scenario | Response |
|----------|----------|
| Missing/invalid bearer token | `401 Unauthorized` |
| Model validation failure (required fields, type errors) | `400 ProblemDetails` — automatic via `[ApiController]` |
| Service-layer business rule violation | `400 ProblemDetails` with `detail` from service error message |
| Resource not found | `404 ProblemDetails` |
| Unhandled exception | `500` (existing exception handler) |

## OpenAPI

Packages added to `KoalaBooks.Web.csproj`:
- `Microsoft.AspNetCore.OpenApi` — generates `/openapi/v1.json`
- `Scalar.AspNetCore` — serves interactive UI at `/scalar/v1` (development only)

All controller actions carry `[ProducesResponseType]` attributes for accurate spec generation.

## Versioning

URL-path versioning: `/api/v1/...`. No versioning library in v1; one can be added when a breaking change requires v2.

## Testing

New integration test class `ApiTests.cs` in `KoalaBooks.Tests`, using the existing `WebApplicationFactory` + Postgres container pattern (same as `OidcTests.cs`). Coverage:

- Unauthenticated request to any endpoint returns `401`
- Authenticated request returns only tenant-scoped data
- `GET /api/v1/fiscal-years` returns the correct list
- `GET /api/v1/fiscal-years/{id}/accounts` returns accounts for that year
- `GET /api/v1/fiscal-years/{fiscalYearId}/journal-entries` returns paginated entries
- `POST /api/v1/fiscal-years/{fiscalYearId}/journal-entries` with valid payload returns `201`
- `POST` with unbalanced lines returns `400`
- `DELETE /api/v1/journal-entries/{id}` returns `204`
