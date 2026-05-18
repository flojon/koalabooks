# KoalaBooks.Components RCL Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move 21 Razor components + 3 CSS isolation files from `KoalaBooks.Web` into a new `KoalaBooks.Components` Razor Class Library so the web app and a future MAUI desktop app can share them.

**Architecture:** New `KoalaBooks.Components` RCL (SDK: `Microsoft.NET.Sdk.Razor`) references Application and Infrastructure; `KoalaBooks.Web` gains a project reference to it. Web-specific bootstrap files (`App.razor`, `Routes.razor`) stay in Web; all layout, page, and shared components move. The Blazor router and SSR endpoint discovery are updated to scan the RCL assembly for page routes.

**Tech Stack:** .NET 10, Blazor Server (InteractiveServer render mode), MudBlazor 9.4.0, `dotnet` CLI, `git mv` for history-preserving moves.

---

## File Map

**Create (new project):**
- `src/KoalaBooks.Components/KoalaBooks.Components.csproj`
- `src/KoalaBooks.Components/_Imports.razor`

**Move from `src/KoalaBooks.Web/Components/` → `src/KoalaBooks.Components/`:**
- `Layout/MainLayout.razor` + `Layout/MainLayout.razor.css`
- `Layout/ReconnectModal.razor` + `Layout/ReconnectModal.razor.css`
- `Shared/AccountSearchDropdown.razor` + `Shared/AccountSearchDropdown.razor.css`
- `Shared/DateInput.razor`
- `Shared/RedirectToLogin.razor`
- `Pages/Accounts.razor`, `Pages/BalanceSheet.razor`, `Pages/BankImport.razor`
- `Pages/Error.razor`, `Pages/FiscalYears.razor`, `Pages/GeneralLedger.razor`
- `Pages/Home.razor`, `Pages/IncomeStatement.razor`, `Pages/Journal.razor`
- `Pages/NotFound.razor`, `Pages/SieExport.razor`, `Pages/SieImport.razor`
- `Pages/SupplierInvoices.razor`, `Pages/Todo.razor`, `Pages/TrialBalance.razor`
- `Pages/VatReport.razor`

**Modify:**
- `KoalaBooks.slnx` — add new project to `/src/` folder
- `src/KoalaBooks.Web/KoalaBooks.Web.csproj` — add project reference to Components
- `src/KoalaBooks.Web/Components/_Imports.razor` — replace with thin version
- `src/KoalaBooks.Web/Components/Routes.razor` — add `AdditionalAssemblies`
- `src/KoalaBooks.Web/Program.cs` — add `.AddAdditionalAssemblies(...)`

---

## Task 1: Create KoalaBooks.Components project

**Files:**
- Create: `src/KoalaBooks.Components/KoalaBooks.Components.csproj`
- Create: `src/KoalaBooks.Components/_Imports.razor`

- [ ] **Step 1: Create the project directory and csproj**

```bash
mkdir -p src/KoalaBooks.Components
```

Create `src/KoalaBooks.Components/KoalaBooks.Components.csproj` with this exact content:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\KoalaBooks.Application\KoalaBooks.Application.csproj" />
    <ProjectReference Include="..\KoalaBooks.Infrastructure\KoalaBooks.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="9.4.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the RCL's `_Imports.razor`**

Create `src/KoalaBooks.Components/_Imports.razor` with this exact content:

```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using KoalaBooks.Components
@using KoalaBooks.Components.Layout
@using KoalaBooks.Components.Shared
@using MudBlazor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
```

- [ ] **Step 3: Register the project in the solution**

Edit `KoalaBooks.slnx`. Inside the `<Folder Name="/src/">` element, add one line (keep alphabetical order):

```xml
    <Project Path="src/KoalaBooks.Components/KoalaBooks.Components.csproj" />
```

The `/src/` folder block should now look like:

```xml
  <Folder Name="/src/">
    <Project Path="src/KoalaBooks.AppHost/KoalaBooks.AppHost.csproj" />
    <Project Path="src/KoalaBooks.Application/KoalaBooks.Application.csproj" />
    <Project Path="src/KoalaBooks.Components/KoalaBooks.Components.csproj" />
    <Project Path="src/KoalaBooks.Domain/KoalaBooks.Domain.csproj" />
    <Project Path="src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj" />
    <Project Path="src/KoalaBooks.ServiceDefaults/KoalaBooks.ServiceDefaults.csproj" />
    <Project Path="src/KoalaBooks.Web/KoalaBooks.Web.csproj" />
  </Folder>
```

- [ ] **Step 4: Add project reference from Web to Components**

In `src/KoalaBooks.Web/KoalaBooks.Web.csproj`, add inside the existing `<ItemGroup>` that holds other project references:

```xml
    <ProjectReference Include="..\KoalaBooks.Components\KoalaBooks.Components.csproj" />
```

- [ ] **Step 5: Verify the new project builds**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: `Build succeeded.` (with only warnings, no errors — it has no Razor files yet so that's fine)

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/ KoalaBooks.slnx src/KoalaBooks.Web/KoalaBooks.Web.csproj
git commit -m "feat: scaffold KoalaBooks.Components RCL and wire into solution (#61)"
```

---

## Task 2: Move Layout components

**Files:**
- Move: `src/KoalaBooks.Web/Components/Layout/MainLayout.razor` → `src/KoalaBooks.Components/Layout/MainLayout.razor`
- Move: `src/KoalaBooks.Web/Components/Layout/MainLayout.razor.css` → `src/KoalaBooks.Components/Layout/MainLayout.razor.css`
- Move: `src/KoalaBooks.Web/Components/Layout/ReconnectModal.razor` → `src/KoalaBooks.Components/Layout/ReconnectModal.razor`
- Move: `src/KoalaBooks.Web/Components/Layout/ReconnectModal.razor.css` → `src/KoalaBooks.Components/Layout/ReconnectModal.razor.css`

- [ ] **Step 1: Move the Layout files using git mv**

```bash
mkdir -p src/KoalaBooks.Components/Layout
git mv src/KoalaBooks.Web/Components/Layout/MainLayout.razor src/KoalaBooks.Components/Layout/MainLayout.razor
git mv src/KoalaBooks.Web/Components/Layout/MainLayout.razor.css src/KoalaBooks.Components/Layout/MainLayout.razor.css
git mv src/KoalaBooks.Web/Components/Layout/ReconnectModal.razor src/KoalaBooks.Components/Layout/ReconnectModal.razor
git mv src/KoalaBooks.Web/Components/Layout/ReconnectModal.razor.css src/KoalaBooks.Components/Layout/ReconnectModal.razor.css
```

- [ ] **Step 2: Build to catch any immediate breakage**

```bash
dotnet build KoalaBooks.slnx 2>&1 | grep -E "error CS|error RZ|Build FAILED|succeeded"
```

Expected: `Build succeeded.` — `App.razor` references `<ReconnectModal />` which is now in the RCL; because Web references Components this still resolves. If you see errors at this stage they'll be namespace mismatches — check that Web's `_Imports.razor` still has `@using KoalaBooks.Web.Components.Layout` (it does — you haven't changed it yet). Those errors will be fixed in Task 5.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor: move Layout components to KoalaBooks.Components (#61)"
```

---

## Task 3: Move Shared components

**Files:**
- Move: `src/KoalaBooks.Web/Components/Shared/AccountSearchDropdown.razor` → `src/KoalaBooks.Components/Shared/AccountSearchDropdown.razor`
- Move: `src/KoalaBooks.Web/Components/Shared/AccountSearchDropdown.razor.css` → `src/KoalaBooks.Components/Shared/AccountSearchDropdown.razor.css`
- Move: `src/KoalaBooks.Web/Components/Shared/DateInput.razor` → `src/KoalaBooks.Components/Shared/DateInput.razor`
- Move: `src/KoalaBooks.Web/Components/Shared/RedirectToLogin.razor` → `src/KoalaBooks.Components/Shared/RedirectToLogin.razor`

- [ ] **Step 1: Move the Shared files using git mv**

```bash
mkdir -p src/KoalaBooks.Components/Shared
git mv src/KoalaBooks.Web/Components/Shared/AccountSearchDropdown.razor src/KoalaBooks.Components/Shared/AccountSearchDropdown.razor
git mv src/KoalaBooks.Web/Components/Shared/AccountSearchDropdown.razor.css src/KoalaBooks.Components/Shared/AccountSearchDropdown.razor.css
git mv src/KoalaBooks.Web/Components/Shared/DateInput.razor src/KoalaBooks.Components/Shared/DateInput.razor
git mv src/KoalaBooks.Web/Components/Shared/RedirectToLogin.razor src/KoalaBooks.Components/Shared/RedirectToLogin.razor
```

- [ ] **Step 2: Build to catch namespace issues early**

```bash
dotnet build KoalaBooks.slnx 2>&1 | grep -E "error CS|error RZ|Build FAILED|succeeded"
```

Expected: `Build succeeded.` — same reasoning as Task 2 Step 2; Web's `_Imports.razor` still has the old `KoalaBooks.Web.Components.Shared` using so it resolves for now.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor: move Shared components to KoalaBooks.Components (#61)"
```

---

## Task 4: Move Pages

**Files:**
- Move all 16 `.razor` files from `src/KoalaBooks.Web/Components/Pages/` → `src/KoalaBooks.Components/Pages/`

- [ ] **Step 1: Move all page components using git mv**

```bash
mkdir -p src/KoalaBooks.Components/Pages
git mv src/KoalaBooks.Web/Components/Pages/Accounts.razor         src/KoalaBooks.Components/Pages/Accounts.razor
git mv src/KoalaBooks.Web/Components/Pages/BalanceSheet.razor     src/KoalaBooks.Components/Pages/BalanceSheet.razor
git mv src/KoalaBooks.Web/Components/Pages/BankImport.razor       src/KoalaBooks.Components/Pages/BankImport.razor
git mv src/KoalaBooks.Web/Components/Pages/Error.razor            src/KoalaBooks.Components/Pages/Error.razor
git mv src/KoalaBooks.Web/Components/Pages/FiscalYears.razor      src/KoalaBooks.Components/Pages/FiscalYears.razor
git mv src/KoalaBooks.Web/Components/Pages/GeneralLedger.razor    src/KoalaBooks.Components/Pages/GeneralLedger.razor
git mv src/KoalaBooks.Web/Components/Pages/Home.razor             src/KoalaBooks.Components/Pages/Home.razor
git mv src/KoalaBooks.Web/Components/Pages/IncomeStatement.razor  src/KoalaBooks.Components/Pages/IncomeStatement.razor
git mv src/KoalaBooks.Web/Components/Pages/Journal.razor          src/KoalaBooks.Components/Pages/Journal.razor
git mv src/KoalaBooks.Web/Components/Pages/NotFound.razor         src/KoalaBooks.Components/Pages/NotFound.razor
git mv src/KoalaBooks.Web/Components/Pages/SieExport.razor        src/KoalaBooks.Components/Pages/SieExport.razor
git mv src/KoalaBooks.Web/Components/Pages/SieImport.razor        src/KoalaBooks.Components/Pages/SieImport.razor
git mv src/KoalaBooks.Web/Components/Pages/SupplierInvoices.razor src/KoalaBooks.Components/Pages/SupplierInvoices.razor
git mv src/KoalaBooks.Web/Components/Pages/Todo.razor             src/KoalaBooks.Components/Pages/Todo.razor
git mv src/KoalaBooks.Web/Components/Pages/TrialBalance.razor     src/KoalaBooks.Components/Pages/TrialBalance.razor
git mv src/KoalaBooks.Web/Components/Pages/VatReport.razor        src/KoalaBooks.Components/Pages/VatReport.razor
```

- [ ] **Step 2: Verify no files remain in Pages (except the directory itself)**

```bash
ls src/KoalaBooks.Web/Components/Pages/ 2>/dev/null && echo "UNEXPECTED FILES" || echo "OK - directory gone or empty"
```

Expected: either "OK" or an empty listing.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor: move Pages to KoalaBooks.Components (#61)"
```

---

## Task 5: Update Web wiring

This task updates the three files in `KoalaBooks.Web` that reference the old component locations, then does a final build to confirm everything compiles.

**Files:**
- Modify: `src/KoalaBooks.Web/Components/_Imports.razor`
- Modify: `src/KoalaBooks.Web/Components/Routes.razor`
- Modify: `src/KoalaBooks.Web/Program.cs`

- [ ] **Step 1: Replace Web's `_Imports.razor` with the thin version**

Overwrite `src/KoalaBooks.Web/Components/_Imports.razor` with exactly this content:

```razor
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using KoalaBooks.Web
@using KoalaBooks.Components
@using KoalaBooks.Components.Layout
@using KoalaBooks.Components.Pages
@using KoalaBooks.Components.Shared
@using MudBlazor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
```

- [ ] **Step 2: Update `Routes.razor` to add `AdditionalAssemblies`**

In `src/KoalaBooks.Web/Components/Routes.razor`, replace the `<Router>` opening tag:

Old:
```razor
    <Router AppAssembly="typeof(Program).Assembly" NotFoundPage="typeof(Pages.NotFound)">
```

New:
```razor
    <Router AppAssembly="typeof(Program).Assembly"
            AdditionalAssemblies="[typeof(KoalaBooks.Components.Pages.Home).Assembly]"
            NotFoundPage="typeof(KoalaBooks.Components.Pages.NotFound)">
```

- [ ] **Step 3: Update `Program.cs` to register the RCL assembly for SSR**

In `src/KoalaBooks.Web/Program.cs`, find this block (around line 161):

```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
```

Replace with:

```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(KoalaBooks.Components.Pages.Home).Assembly);
```

- [ ] **Step 4: Build the full solution — must succeed with no errors**

```bash
dotnet build KoalaBooks.slnx 2>&1 | grep -E "error CS|error RZ|Build FAILED|Build succeeded"
```

Expected: `Build succeeded.`

If you see `error CS0234` (type not found) or `error RZ` (Razor compile error), the most likely cause is a stale `@using` pointing at `KoalaBooks.Web.Components.*` — search all `.razor` files in `src/KoalaBooks.Web/` for any remaining `KoalaBooks.Web.Components` references and update them to `KoalaBooks.Components`.

- [ ] **Step 5: Run the test suite**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj --no-build 2>&1 | tail -5
```

Expected: all tests pass (this refactor touches no application logic).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Web/Components/_Imports.razor \
        src/KoalaBooks.Web/Components/Routes.razor \
        src/KoalaBooks.Web/Program.cs
git commit -m "feat: wire KoalaBooks.Components into Web router and SSR pipeline (#61)"
```

---

## Task 6: Smoke-test and final commit

- [ ] **Step 1: Confirm no Razor files remain in Web/Components/ subfolders**

```bash
find src/KoalaBooks.Web/Components -name "*.razor" | sort
```

Expected output (exactly these three files, nothing else):
```
src/KoalaBooks.Web/Components/App.razor
src/KoalaBooks.Web/Components/Routes.razor
src/KoalaBooks.Web/Components/_Imports.razor
```

- [ ] **Step 2: Confirm all moved files exist in the RCL**

```bash
find src/KoalaBooks.Components -name "*.razor" -o -name "*.razor.css" | sort | wc -l
```

Expected: `25` (21 `.razor` + 3 `.razor.css` + 1 `_Imports.razor`)

- [ ] **Step 3: Final solution build**

```bash
dotnet build KoalaBooks.slnx 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 4: Start the app and verify it loads**

```bash
dotnet run --project src/KoalaBooks.Web/KoalaBooks.Web.csproj
```

Open `https://localhost:5001` (or the port shown in the console). Verify:
- Login page renders
- After login, the home page (`/`) loads
- Navigation to at least one data page (e.g. `/journal`) renders without error

Stop with `Ctrl+C`.

- [ ] **Step 5: Push the branch and open a PR**

```bash
git push -u origin worktree-feat+issue-61-components-rcl
gh pr create \
  --title "feat: extract Razor components to KoalaBooks.Components RCL (#61)" \
  --body "$(cat <<'EOF'
## Summary

- New `KoalaBooks.Components` Razor Class Library (`Microsoft.NET.Sdk.Razor`, net10.0)
- 21 Razor files + 3 CSS isolation files moved from `KoalaBooks.Web/Components/` to the RCL
- `App.razor` and `Routes.razor` remain in Web (HTML shell and router are web-only)
- `_Imports.razor` split: RCL holds the full shared set; Web has a thin version covering only `App.razor` and `Routes.razor`
- `Routes.razor` and `Program.cs` updated to register the RCL assembly for page-route discovery
- No logic changes — pure file move

## Follow-up

- #79 will remove the `KoalaBooks.Infrastructure` reference from the RCL by introducing service interfaces in `KoalaBooks.Application`

## Test plan

- [ ] `dotnet build KoalaBooks.slnx` succeeds with no errors
- [ ] All existing tests pass (`dotnet test`)
- [ ] Web app starts and all pages render identically to before

Closes #61

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
