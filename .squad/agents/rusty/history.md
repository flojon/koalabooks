# Rusty — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Frontend Dev
- **Joined:** 2026-04-15T15:25:43.795Z

## Learnings

<!-- Append learnings below -->
- **P0 #6 — eval() XSS fix (2026-04-15):** Replaced `eval()` in `SieExport.razor` (line 83) with proper JS interop via `IJSRuntime.InvokeVoidAsync("downloadFileFromBase64", ...)`. Created `wwwroot/js/download.js` with a safe `downloadFileFromBase64` function that builds an anchor element for file download. Script loaded in `App.razor` after the Blazor framework script.
- **JS interop pattern:** For file downloads in this project, use `downloadFileFromBase64(base64, fileName)` from `wwwroot/js/download.js`. Extend this file for future JS interop needs.
- **Key paths:** `src/KoalaBooks.Web/Components/Pages/SieExport.razor`, `src/KoalaBooks.Web/wwwroot/js/download.js`, `src/KoalaBooks.Web/Components/App.razor`
- **Searchable account dropdown (2026-04-15):** Created `Components/Shared/AccountSearchDropdown.razor` — a pure Blazor reusable combobox (no JS). Supports two-way binding via `@bind-SelectedAccountId`, filters by account number OR name (case-insensitive), keyboard nav (arrows/Enter/Escape). Scoped CSS in `.razor.css` matches the app's design tokens. Used in `Journal.razor` replacing the old `<select>`. Added `@using KoalaBooks.Web.Components.Shared` to `_Imports.razor` so the component is available project-wide.
- **Delete draft journal entries UI (2026-04-17):** Added 🗑️ Delete button (btn-danger, btn-sm) for draft entries in `Journal.razor`, next to Edit and Post. Uses inline "Are you sure?" confirmation pattern (mirroring the reversal flow) with `_deletingEntryId` state. Calls `JournalEntryService.DeleteDraftAsync(entryId)` — shows error from service on failure, success message + list refresh on success. No new components or JS needed.
- **P0 batch 2 — code review fixes (2026-04-17):** Fixed 3 issues from Danny's review:
  1. **FiscalYears.razor closing flow:** Replaced the bare "Close" button (which only flipped `IsClosed`) with a full inline closing flow using `YearEndClosingService`. Flow: click "Stäng bokslut" → preview (revenue/expenses/net result + closing entry lines) → confirm → execute. Validation errors shown inline, success message includes closing entry numbers.
  2. **Journal.razor null crash:** Fixed `_activeFiscalYear!.Id` NRE in `OnInitializedAsync` — now returns early if `GetActiveAsync()` is null, letting the render template show the info alert.
  3. **MainLayout ErrorBoundary:** Wrapped `@Body` in `<ErrorBoundary>` with user-friendly error message and "Try again" recover button. Unhandled exceptions no longer crash the whole app.
- **UX Phase 2 — Snackbar + Loading States (2026-04-18):** Replaced all `_error`/`_success` inline alert patterns with `ISnackbar.Add(...)` across Journal, Accounts, FiscalYears, SieImport, SieExport. Persistent guidance messages (no fiscal year, no data) converted to `<MudAlert>`. Added `_isLoading` + `<MudProgressLinear>` to Journal, Accounts, GeneralLedger. Created `NotificationService` wrapper (scoped). Configured snackbar globally: BottomRight, max 3, 3s default. Removed `.alert` CSS classes from `app.css`. Clean build 0 warnings.
