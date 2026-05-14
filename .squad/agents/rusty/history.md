# Rusty — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Frontend Dev
- **Joined:** 2026-04-15T15:25:43.795Z

## Learnings

<!-- Append learnings below -->
- **AccountClass localization in UI (2026-07-25):** Updated Accounts.razor to use ToLocalizedString for AccountClass in both the table and form dropdown. This ensures Swedish and other translations are shown based on the current UI culture. Pattern: always use enum extension ToLocalizedString for display, not .ToString(). Key file: Components/Pages/Accounts.razor.
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
- **Swedish UI Translation (2026-04-18):** Translated ALL user-facing text from English to Swedish across 11 Razor files: MainLayout, Home, Accounts, Journal, FiscalYears, TrialBalance, IncomeStatement, BalanceSheet, GeneralLedger, SieImport, SieExport. Also set `<html lang="sv">` in App.razor. Translations cover: nav menu labels, page titles (`<PageTitle>`), headings, button text, table headers, form labels, placeholder text, alert/snackbar messages, error messages, and status labels. Technical terms (variable names, CSS classes, account numbers) left in English. BasImport.razor was not found on disk (possibly removed by another agent). Build: 0 warnings, 0 errors.
- **FiscalYears overlap error handling (2026-07-24):** Added try-catch around `FiscalYearService.CreateAsync` in `FiscalYears.razor` to catch `InvalidOperationException` thrown by Livingston's overlap validation. Shows Swedish snackbar error message "Räkenskapsåret överlappar med ett befintligt räkenskapsår." via the already-injected `ISnackbar`. Build: 0 errors.
- **Journal account dropdown bugfixes (2026-07-24):** Fixed two bugs in AccountSearchDropdown:
  1. **Dropdown clipped by table overflow:** The global `table` CSS had `overflow: hidden` (for border-radius), which clipped the absolutely-positioned dropdown list. Fixed by adding `style="overflow:visible"` on the form table in `Journal.razor`. Also bumped dropdown z-index from 50 to 1000 in the scoped CSS.
  2. **Search not filtering as you type:** The `@bind` directive defaulted to `onchange` (fires on blur). Added `@bind:event="oninput"` so filtering happens on every keystroke.
  3. Translated remaining English strings in AccountSearchDropdown (placeholder + no-results message) to Swedish.
