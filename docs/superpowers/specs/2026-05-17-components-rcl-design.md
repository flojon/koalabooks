# Design: Extract Razor Components to KoalaBooks.Components RCL

**Issue:** #61 (part of #60)
**Date:** 2026-05-17

## Goal

Create a new `KoalaBooks.Components` Razor Class Library and move the portable Razor components out of `KoalaBooks.Web`. Both the existing web app and a future MAUI desktop app (#63) will reference this RCL so UI changes ship to both targets without duplication.

## What moves vs what stays

**Stays in `KoalaBooks.Web/Components/`:**

| File | Reason |
|------|--------|
| `App.razor` | HTML shell (`<!DOCTYPE html>`, `<head>`, `<body>`, Blazor script tags) — web-only |
| `Routes.razor` | Blazor Web router, references `typeof(Program).Assembly` — web-only |
| `_Imports.razor` | Replaced with a thin version scoped to the two files above |

**Moves to `KoalaBooks.Components/` (21 `.razor` + 3 `.razor.css`):**

- `Layout/MainLayout.razor` + `.razor.css`
- `Layout/ReconnectModal.razor` + `.razor.css`
- `Pages/` — all 16 page components
- `Shared/AccountSearchDropdown.razor` + `.razor.css`
- `Shared/DateInput.razor`
- `Shared/RedirectToLogin.razor`

## New project: KoalaBooks.Components

**Path:** `src/KoalaBooks.Components/KoalaBooks.Components.csproj`

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

The Infrastructure reference is required because five pages (`Accounts`, `BankImport`, `SieImport`, `SieExport`, `Todo`) currently inject concrete types from `KoalaBooks.Infrastructure.Services`. This is tracked as tech debt in #79, which will introduce service interfaces in Application and eliminate the Infrastructure dependency.

## Namespace

Root namespace: `KoalaBooks.Components`. Subfolder namespaces follow automatically:
- `KoalaBooks.Components.Layout`
- `KoalaBooks.Components.Pages`
- `KoalaBooks.Components.Shared`

No `@namespace` directives needed in individual files — the SDK derives them from folder structure.

## _Imports.razor split

**RCL `_Imports.razor`** — full set for all moved components:

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

**Web `_Imports.razor`** — only what `App.razor` and `Routes.razor` need:

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

(`System.Net.Http` / `System.Net.Http.Json` drop out — neither `App.razor` nor `Routes.razor` uses `HttpClient`.)

## Runtime wiring changes

**`Routes.razor`** — add `AdditionalAssemblies` so the Blazor router discovers pages in the RCL, and update the `NotFoundPage` type reference:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="[typeof(KoalaBooks.Components.Pages.Home).Assembly]"
        NotFoundPage="typeof(KoalaBooks.Components.Pages.NotFound)">
```

**`Program.cs`** — add `.AddAdditionalAssemblies(...)` so SSR endpoint discovery finds RCL pages:

```csharp
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(KoalaBooks.Components.Pages.Home).Assembly);
```

**`KoalaBooks.Web.csproj`** — add project reference:

```xml
<ProjectReference Include="..\KoalaBooks.Components\KoalaBooks.Components.csproj" />
```

**`KoalaBooks.slnx`** — register the new project in the solution.

## No logic changes

This is a pure file move. No component logic, routing, or service registrations change. The web app must build and behave identically before and after.

## Tech debt

- **#79** — Extract service interfaces from `KoalaBooks.Infrastructure.Services` to `KoalaBooks.Application`, allowing `KoalaBooks.Components` to drop its Infrastructure reference. Blocked by this issue.
