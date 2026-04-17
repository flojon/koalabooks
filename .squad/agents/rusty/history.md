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
