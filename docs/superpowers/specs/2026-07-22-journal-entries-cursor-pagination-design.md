# Cursor-based pagination for JournalEntriesController.GetAll (#343)

## Background

Issue #122 (public REST API coverage) listed cursor-based pagination under its
Infrastructure section — replace in-memory `Skip/Take` on journal entries —
but this item was never assigned to any of the #122 agent streams and was
never delivered. `JournalEntriesController.GetByFiscalYear` still calls
`IJournalEntryService.GetByFiscalYearAsync`, which loads *every* matching
entry into memory via `ToListAsync()`, and then the controller does
`.Skip((page-1)*pageSize).Take(pageSize)` on the resulting in-memory
`List<JournalEntry>`. Every page request re-fetches and re-materializes the
entire fiscal year's journal entries regardless of which page is requested.

`Journal.razor` has the same underlying pattern one layer up: it calls
`GetByFiscalYearAsync` with no `from`/`to`, loads the whole fiscal year's
posted entries into `_entries`, and then applies a month filter client-side
(`_entries.Where(e => e.Date.Month == SelectedMonth.Value)`). It renders the
full list with no pagination UI at all today.

## Goal

Replace the in-memory materialize-then-slice pattern with pagination pushed
down into the EF query (`OrderBy` + `Skip`/`Take` executed as Postgres
`OFFSET`/`LIMIT`), and give `Journal.razor` a real paged UI: a selectable
page size (25/50/100), a sort order (entry number or date), and a working
month filter that is sent to the server instead of applied after the fact.

## Why not true keyset/cursor pagination

The issue's title says "cursor-based," and `(FiscalYearId, EntryNumber)` is a
unique, already-ordered index that would make a classic keyset cursor
(`WHERE EntryNumber > cursor ORDER BY EntryNumber LIMIT n`) cheap and
gap-safe. This was the first design considered. It broke down for two
reasons surfaced during design review:

1. **Numbered page buttons need O(1) jump-to-page-N.** A keyset cursor can
   only chain forward from a cursor you've already seen; direct entry-number
   math (`cursor = (page-1) * pageSize`) only works because `EntryNumber` is
   *assumed dense*. It is not always dense (the app already tracks entry
   number gaps via `VoucherGapService` for deleted drafts, voided imports,
   etc.), and it stops being a useful proxy for row position entirely once a
   `from`/`to` date filter (i.e. the month filter) narrows the set — a
   month's entries are not evenly distributed across the full year's entry
   numbers.
2. **Sorting by date has no dense integer proxy at all.** A date-sorted
   cursor also only supports forward chaining, not direct page jump.

The only way to keep numbered pages working uniformly for both sort orders
and under an active month filter was to fall back to Next/Previous-only
navigation whenever date-sort or a filter was active, with numbered buttons
only in the unfiltered, entry-number-sorted case. That inconsistency was
judged not worth it: the actual bug being fixed is the in-memory
materialization, not the specific pagination *mechanism*, and standard
`OFFSET`/`LIMIT` pushed into the SQL query fixes that bug just as well while
giving numbered pages uniformly, for any sort order and any filter, with no
special-casing. At this app's per-fiscal-year data volumes (an accounting
journal — thousands, not millions, of entries per year), `OFFSET` depth is
not a real performance concern.

This design therefore uses DB-level offset pagination, not keyset cursors,
despite the issue's title — the goal (stop loading everything into memory)
is met; the literal mechanism named in the title is not.

## Design

### Domain (`KoalaBooks.Domain`)

```csharp
namespace KoalaBooks.Domain.Interfaces;

public enum JournalEntrySortBy { EntryNumber, Date }

public class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
}
```

`IJournalEntryService.GetByFiscalYearAsync` changes from returning
`Task<List<JournalEntry>>` to:

```csharp
Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
    int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
    string? search = null,
    JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
    int page = 1, int pageSize = 50);
```

`search`, when provided, filters to entries whose `EntryNumber` exactly
matches (if `search` parses as an integer) or whose `Description` contains
it (case-insensitive). This exists so every caller — including a picker
that previously wanted "the whole fiscal year" — can stay page-bounded by
searching instead of materializing the full set. There is deliberately no
"get everything" escape hatch (no `IJournalEntryServiceExtensions`,
no loop-until-`TotalCount` helper): every caller of `GetByFiscalYearAsync`,
across both the InteractiveServer and WASM/InteractiveAuto render paths,
is expected to work against a single bounded page.

### Application (`JournalEntryService`)

```csharp
public async Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
    int fiscalYearId, DateOnly? from, DateOnly? to,
    string? search, JournalEntrySortBy sortBy, int page, int pageSize)
{
    var query = _db.JournalEntries
        .Include(j => j.Lines).ThenInclude(l => l.Account)
        .Where(j => j.FiscalYearId == fiscalYearId);

    if (from.HasValue) query = query.Where(j => j.Date >= from.Value);
    if (to.HasValue) query = query.Where(j => j.Date <= to.Value);

    if (!string.IsNullOrWhiteSpace(search))
    {
        var s = search.Trim();
        query = int.TryParse(s, out var entryNumber)
            ? query.Where(j => j.EntryNumber == entryNumber || EF.Functions.ILike(j.Description, $"%{s}%"))
            : query.Where(j => EF.Functions.ILike(j.Description, $"%{s}%"));
    }

    query = sortBy switch
    {
        JournalEntrySortBy.Date => query.OrderBy(j => j.Date).ThenBy(j => j.EntryNumber),
        _ => query.OrderBy(j => j.EntryNumber)
    };

    var totalCount = await query.CountAsync();
    var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

    return new PagedResult<JournalEntry>
    {
        Items = items, Page = page, PageSize = pageSize, TotalCount = totalCount
    };
}
```

Two DB round-trips (`CountAsync` + the page query); no full materialization
of the fiscal year's entries.

### Web (`KoalaBooks.Web`)

`Web/Models/Api/PagedResult<T>` (Items/Page/PageSize/TotalCount) already
exists and matches the Domain type's shape — the wire format is unchanged.
`JournalEntriesController.GetByFiscalYear` gains two new query parameters:

```
GET /api/v1/fiscal-years/{fiscalYearId}/journal-entries
    ?from=&to=&search=&sortBy=entryNumber|date&page=&pageSize=
```

- `sortBy` defaults to `entryNumber`; unrecognized values → `400 Bad Request`.
- `search` is optional and passed through as-is to
  `GetByFiscalYearAsync` (entry-number exact match or description
  substring, per above).
- `pageSize` stays server-clamped to 1–200 (unchanged), independent of the
  UI's 25/50/100 selector — external API consumers aren't restricted to
  those three values.
- `page` stays clamped to a minimum of 1 (unchanged).

### `Journal.razor`

- Add a page-size selector (25/50/100, default 50) and a sort selector
  (Entry # default / Date).
- Replace "load all posted entries for the fiscal year, then filter/slice
  client-side" with a real per-page fetch using the new
  `GetByFiscalYearAsync` signature.
- The existing month filter (`_selectedMonthStr`) is converted from a
  client-side `.Where(e => e.Date.Month == ...)` into real `from`/`to`
  values (first/last day of the selected month) sent to the server.
- Numbered page buttons rendered from `Page`/`PageSize`/`TotalCount` in the
  response — works identically for any sort order or filter combination,
  since it's plain offset pagination underneath.
- Changing the month filter, sort order, or page size resets to page 1.

### `ClassifyDocumentDialog.razor`

UX change: the linkable-entry picker is currently a plain `<select>`
(`ClassifyDocumentDialog.razor:187-193`) populated from
`_linkableEntries`, a full-fiscal-year load (`:290`). It becomes a
`MudAutocomplete<JournalEntry>` with a debounced `SearchFunc`
(`MinCharacters="0"` so it's browsable before typing, `DebounceInterval`
per Mud defaults) that calls
`GetByFiscalYearAsync(_fiscalYear.Id, search: text, page: 1, pageSize: 20)`
on each keystroke and renders the returned page as the option list. There
is no local `_linkableEntries` list anymore, no "load everything up
front," and no loading spinner gating the whole dialog — each keystroke's
request is its own bounded page. `ToStringFunc` renders
`#{EntryNumber} {Date:yyyy-MM-dd} — {Description}`, matching the current
option text.

### WASM client (`JournalEntryApiService`)

Updated to match the new `IJournalEntryService.GetByFiscalYearAsync`
signature: passes `from/to/search/sortBy/page/pageSize` through as query
params against the REST endpoint, and parses the (unchanged-shape)
`PagedResult<JournalEntryResponse>` response into the Domain
`PagedResult<JournalEntry>`. Since no caller anywhere (Domain, Web, or
Client) needs "the whole fiscal year" anymore, this becomes a direct,
unremarkable 1:1 mapping of the paged endpoint — the previous
`pageSize=200`-as-stand-in-for-everything hack
(`JournalEntryApiService.cs:13-19` today) is deleted outright rather than
patched, closing the InteractiveAuto truncation risk by removing the
pattern instead of chasing it into a second layer.

## Out of scope

`SupplierInvoicesController`/`BankTransactionsController` (and their backing
services) have the identical in-memory materialize-then-slice pattern, but
neither is named in issue #343. Worth a follow-up issue, not fixed here.

## Testing

Integration tests via `WebApiFactory` + Testcontainers Postgres, matching
the #122 program convention:

- `JournalEntries_List_ReturnsPaginatedResult` and
  `JournalEntries_List_UnknownFiscalYear_Returns404` (existing) still apply
  as-is — the response envelope shape is unchanged.
- `sortBy=date` returns entries ordered by date (entry number as tiebreak
  within a date).
- An unrecognized `sortBy` value returns `400`.
- A `from`/`to` range (simulating the month filter) returns the correct
  slice with correct `TotalCount`, and paging through multiple pages under
  a filter produces no duplicate or missing entries.
- `search` matching an exact `EntryNumber` returns that entry; `search`
  matching a `Description` substring (case-insensitive) returns the
  matching entries; a `search` matching neither returns an empty page
  with `TotalCount: 0`, not an error.
- `page`/`pageSize` clamping behaves as before (`pageSize` 1–200, `page`
  minimum 1).
- Unit/service-level test (or an EF query assertion) confirming the query
  is not materialized before `Skip/Take` — e.g. asserting the returned
  `Items.Count` never exceeds `pageSize` regardless of total entries, is a
  weaker but acceptable proxy if a query-plan-level assertion isn't
  practical in the test harness.
