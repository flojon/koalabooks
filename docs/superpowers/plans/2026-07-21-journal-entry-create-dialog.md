# Journal entry creation as a popup dialog (#197) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `Journal.razor`'s inline "Ny verifikation" card with a `NewJournalEntryDialog` popup (via `IDialogService`), and let the user stage file attachments that get uploaded and linked right after the entry is created.

**Architecture:** One new self-contained dialog component, `Shared/NewJournalEntryDialog.razor`, following the existing `ReversalPreviewDialog.razor`/`ClassifyDocumentDialog.razor` pattern (plain `<MudDialog>` root, `IMudDialogInstance` cascading parameter, opened via `DialogService.ShowAsync`). `Journal.razor` loses all of its inline-form state and delegates entirely to the dialog, reading back a small result record to drive its snackbar messages and reload.

**Tech Stack:** Blazor Server, MudBlazor (`MudDialog`, `MudFileUpload`, `MudAlert`), bUnit + NSubstitute for component tests.

## Global Constraints

- Dialog follows `ReversalPreviewDialog.razor`'s structure exactly: plain `<MudDialog>` root, `[CascadingParameter] IMudDialogInstance MudDialog`, no `Visible`/`@ref`, own `<TitleContent>`.
- Opened with `DialogDefaults.NoDismiss` (`src/KoalaBooks.Components/Shared/DialogDefaults.cs`) — blocks backdrop-click/Escape dismissal, same as the other two dialogs.
- Attachment upload: 10 MB per-file limit (`const long maxBytes = 10 * 1024 * 1024;`), content-type fallback `string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType` — exact match to `Journal.razor`'s existing `UploadAttachmentAsync`. No `Accept`/`MaximumFileCount` restriction on the `MudFileUpload`.
- Swedish UI copy, verbatim: dialog title `"Ny verifikation"`, buttons `"💾 Bokför"` / `"Spara som utkast"` / `"Avbryt"`, snackbar texts as shown in Task 2.
- `NewJournalEntryDialog.NewEntryResult` record: `(JournalEntry Entry, bool Posted, List<string> FailedFiles)` — the only channel back to `Journal.razor`.
- A single failed attachment upload must **not** block entry creation/posting or abort uploading the remaining files — collect failures, still close the dialog with `DialogResult.Ok`.
- bUnit cannot reliably drive a real `IBrowserFile` selection through `MudFileUpload`'s wrapped `<InputFile>` in this codebase's test setup (confirmed convention — see `PreviewDocumentDialogTests.cs`'s own comments on what it does and doesn't cover). The attachment upload/failure scenarios are therefore covered only by manual Playwright (Task 2's verification steps), not bUnit — this mirrors the spec's own scoping of file-upload testing to Playwright.

---

## Task 1: `NewJournalEntryDialog` component + bUnit tests

**Files:**
- Create: `src/KoalaBooks.Components/Shared/NewJournalEntryDialog.razor`
- Test: `tests/KoalaBooks.ComponentTests/NewJournalEntryDialogTests.cs`

**Interfaces:**
- Consumes: `JournalEntryForm` (`Shared/JournalEntryForm.razor`) — parameters `Accounts`, `Date`/`DateChanged`, `Description`/`DescriptionChanged`, `Lines` (`List<JournalEntryForm.LineModel>`), `IsBalancedChanged`, `DirtyChanged` (all already exist, unchanged). `UnsavedChangesGuard` (`Shared/UnsavedChangesGuard.razor`) — parameter `IsDirty`. `IJournalEntryService.CreateAsync(JournalEntry)` → `(JournalEntry? Entry, string? Error)`, `IJournalEntryService.PostAsync(int)` → `string? Error`. `IDocumentService.UploadAndLinkAsync(string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId)` → `(Document? Doc, string? Error)`.
- Produces: `NewJournalEntryDialog.NewEntryResult` record — `JournalEntry Entry, bool Posted, List<string> FailedFiles` — consumed by Task 2's `OpenNewEntryDialogAsync`. Public parameters `Accounts` (`List<Account>`, `[EditorRequired]`) and `FiscalYearId` (`int`, `[EditorRequired]`) — Task 2 supplies both.

- [x] **Step 1: Create the component skeleton (compiles, not yet wired to services)**

Create `src/KoalaBooks.Components/Shared/NewJournalEntryDialog.razor`:

```razor
@* src/KoalaBooks.Components/Shared/NewJournalEntryDialog.razor *@
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Enums
@using KoalaBooks.Domain.Interfaces
@using MudBlazor
@using Microsoft.AspNetCore.Components.Forms

<UnsavedChangesGuard IsDirty="_isDirty" />

<MudDialog Style="max-width:640px; width:95vw;">
    <TitleContent>
        <MudText Typo="Typo.h6">Ny verifikation</MudText>
    </TitleContent>
    <DialogContent>
        <JournalEntryForm Accounts="Accounts"
                          Date="_date" DateChanged="d => _date = d"
                          Description="@_description" DescriptionChanged="d => _description = d"
                          Lines="_lines"
                          IsBalancedChanged="b => _isBalanced = b"
                          DirtyChanged="MarkDirty" />

        <MudFileUpload T="IReadOnlyList<IBrowserFile>" FilesChanged="files => _pendingFiles.AddRange(files)">
            <CustomContent>
                <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.AttachFile"
                           OnClick="@context.OpenFilePickerAsync">
                    Bifoga fil
                </MudButton>
            </CustomContent>
        </MudFileUpload>

        @if (_pendingFiles.Count > 0)
        {
            <div style="display:flex; flex-wrap:wrap; gap:0.4rem; margin-top:0.5rem;">
                @for (int i = 0; i < _pendingFiles.Count; i++)
                {
                    var idx = i;
                    <span style="display:inline-flex; align-items:center; gap:0.35rem; background:#f1f5f9; border-radius:1rem; padding:0.15rem 0.6rem; font-size:0.8rem;">
                        @_pendingFiles[idx].Name
                        <button type="button" class="btn btn-sm" style="padding:0 0.25rem; line-height:1;"
                                @onclick="() => _pendingFiles.RemoveAt(idx)">✕</button>
                    </span>
                }
            </div>
        }

        @if (_error is not null)
        {
            <MudAlert Severity="Severity.Error" Dense="true" Class="mt-2">@_error</MudAlert>
        }
    </DialogContent>
    <DialogActions>
        <button class="btn btn-secondary" @onclick="MudDialog.Cancel" disabled="@_saving">Avbryt</button>
        <button class="btn btn-secondary" @onclick="SaveAsDraftAsync" disabled="@(!_isBalanced || _saving)">Spara som utkast</button>
        <button class="btn btn-success" @onclick="SaveAndPostAsync" disabled="@(!_isBalanced || _saving)">💾 Bokför</button>
    </DialogActions>
</MudDialog>

@code {
    public record NewEntryResult(JournalEntry Entry, bool Posted, List<string> FailedFiles);

    [Parameter, EditorRequired] public List<Account> Accounts { get; set; } = [];
    [Parameter, EditorRequired] public int FiscalYearId { get; set; }

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Inject] private IJournalEntryService JournalEntryService { get; set; } = default!;
    [Inject] private IDocumentService DocumentService { get; set; } = default!;

    private DateTime _date = DateTime.Today;
    private string _description = "";
    private List<JournalEntryForm.LineModel> _lines = [new(), new()];
    private bool _isBalanced;
    private bool _isDirty;
    private List<IBrowserFile> _pendingFiles = [];
    private List<string> _failedFiles = [];
    private string? _error;
    private bool _saving;

    private void MarkDirty() => _isDirty = true;

    private async Task SaveAndPostAsync() => await SaveAsync(post: true);

    private async Task SaveAsDraftAsync() => await SaveAsync(post: false);

    private async Task SaveAsync(bool post)
    {
        if (_saving) return;
    }
}
```

This compiles and renders, but `SaveAsync` is still a no-op — the tests in Step 2 exercise it and fail there.

- [x] **Step 2: Write the failing bUnit tests**

Create `tests/KoalaBooks.ComponentTests/NewJournalEntryDialogTests.cs`:

```csharp
using System.Linq;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// MudDialog only renders its content when hosted by a real MudDialogProvider/IDialogService
// (see PreviewDocumentDialogTests.cs), so these tests open the dialog the same way
// Journal.razor does. File-attachment scenarios (staging/removing/upload-failure) are not
// covered here — bUnit cannot reliably drive a real IBrowserFile selection through
// MudFileUpload's wrapped <InputFile> in this codebase's test setup; that's covered by
// manual Playwright verification instead (see the plan's Global Constraints).
public class NewJournalEntryDialogTests : BunitContext, IAsyncLifetime
{
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();

    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public NewJournalEntryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_documentService);
    }

    private static List<Account> MakeAccounts() =>
    [
        new() { Id = 1, AccountNumber = "1930", Name = "Företagskonto", IsActive = true },
        new() { Id = 2, AccountNumber = "4010", Name = "Inköp material", IsActive = true },
    ];

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> OpenDialogAsync()
    {
        var comp = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<NewJournalEntryDialog>
        {
            { x => x.Accounts, MakeAccounts() },
            { x => x.FiscalYearId, 7 },
        };

        IDialogReference? reference = null;
        await comp.InvokeAsync(async () =>
            reference = await dialogService.ShowAsync<NewJournalEntryDialog>("Ny verifikation", parameters, DialogDefaults.NoDismiss));

        return (comp, reference!);
    }

    private static AngleSharp.Dom.IElement FindButton(IRenderedComponent<MudDialogProvider> comp, string text) =>
        comp.FindAll("button").Single(b => b.TextContent == text);

    // Balances the two default empty lines: debit on line 0, credit on line 1.
    private static void BalanceLines(IRenderedComponent<MudDialogProvider> comp)
    {
        var numberInputs = comp.FindAll("input[type=number]");
        numberInputs[0].Change("100"); // line 0 debit
        numberInputs[3].Change("100"); // line 1 credit
    }

    [Fact]
    public async Task RendersForm_WithTwoEmptyLines_AndSaveButtonsDisabled()
    {
        var (comp, _) = await OpenDialogAsync();

        Assert.NotNull(FindButton(comp, "💾 Bokför").GetAttribute("disabled"));
        Assert.NotNull(FindButton(comp, "Spara som utkast").GetAttribute("disabled"));
        Assert.Equal(2, comp.FindAll("input[type=number]").Count / 2);
    }

    [Fact]
    public async Task EditingDescription_MarksDialogDirty()
    {
        var (comp, _) = await OpenDialogAsync();

        Assert.False(comp.FindComponent<UnsavedChangesGuard>().Instance.IsDirty);

        comp.Find("input[placeholder='Beskrivning av transaktion']").Change("Kontorsmaterial");

        Assert.True(comp.FindComponent<UnsavedChangesGuard>().Instance.IsDirty);
    }

    [Fact]
    public async Task ClickingBokfor_WhenCreateFails_KeepsDialogOpen_AndShowsError()
    {
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>())
            .Returns(((JournalEntry?)null, "Kunde inte skapa verifikationen."));
        var (comp, dialogReference) = await OpenDialogAsync();
        BalanceLines(comp);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        Assert.False(dialogReference.Result.IsCompleted);
        Assert.Contains("Kunde inte skapa verifikationen.", comp.Markup);
    }

    [Fact]
    public async Task ClickingBokfor_WhenCreateAndPostSucceed_ClosesWithResult()
    {
        var created = new JournalEntry { Id = 55, EntryNumber = 12, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        _journalEntryService.PostAsync(55).Returns((string?)null);
        var (comp, dialogReference) = await OpenDialogAsync();
        BalanceLines(comp);

        await comp.InvokeAsync(() => FindButton(comp, "💾 Bokför").Click());

        var result = await dialogReference.Result;
        Assert.False(result!.Canceled);
        var data = Assert.IsType<NewJournalEntryDialog.NewEntryResult>(result.Data);
        Assert.Same(created, data.Entry);
        Assert.True(data.Posted);
        Assert.Empty(data.FailedFiles);
        await _documentService.DidNotReceive().UploadAndLinkAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Func<Stream>>(), Arg.Any<DocumentEntityType>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ClickingSparaSomUtkast_WhenCreateSucceeds_ClosesWithPostedFalse()
    {
        var created = new JournalEntry { Id = 56, EntryNumber = 13, Description = "x", FiscalYearId = 7 };
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>()).Returns((created, (string?)null));
        var (comp, dialogReference) = await OpenDialogAsync();
        BalanceLines(comp);

        await comp.InvokeAsync(() => FindButton(comp, "Spara som utkast").Click());

        var result = await dialogReference.Result;
        var data = Assert.IsType<NewJournalEntryDialog.NewEntryResult>(result!.Data);
        Assert.False(data.Posted);
        await _journalEntryService.DidNotReceive().PostAsync(Arg.Any<int>());
    }
}
```

- [x] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj --filter "FullyQualifiedName~NewJournalEntryDialogTests"`
Expected: FAIL — `RendersForm_WithTwoEmptyLines_AndSaveButtonsDisabled` and `EditingDescription_MarksDialogDirty` should already pass (skeleton renders and wires `DirtyChanged`/`IsBalancedChanged`), but the three save-flow tests FAIL because `SaveAsync` is a no-op (`CreateAsync` never called, dialog never closes).

- [x] **Step 4: Implement `SaveAsync`**

Replace the `SaveAsync` method body in `NewJournalEntryDialog.razor`'s `@code` block:

```csharp
    private async Task SaveAsync(bool post)
    {
        if (_saving) return;
        _error = null;
        _saving = true;
        try
        {
            var entry = new JournalEntry
            {
                Date = DateOnly.FromDateTime(_date),
                Description = _description,
                FiscalYearId = FiscalYearId,
                Lines = _lines.Select(l => new JournalEntryLine
                {
                    AccountId = l.AccountId,
                    DebitAmount = l.DebitAmount,
                    CreditAmount = l.CreditAmount
                }).ToList()
            };

            var (result, error) = await JournalEntryService.CreateAsync(entry);
            if (error is not null)
            {
                _error = error;
                return;
            }

            if (post)
            {
                // Create and post are separate calls with no wrapping transaction, matching
                // Journal.razor's previous inline SaveEntryAsync behavior.
                var postError = await JournalEntryService.PostAsync(result!.Id);
                if (postError is not null)
                {
                    _error = postError;
                    return;
                }
            }

            const long maxBytes = 10 * 1024 * 1024;
            _failedFiles = [];
            foreach (var file in _pendingFiles)
            {
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                var (added, _) = await DocumentService.UploadAndLinkAsync(
                    file.Name, contentType, () => file.OpenReadStream(maxBytes),
                    DocumentEntityType.JournalEntry, result!.Id);
                if (added is null)
                {
                    _failedFiles.Add(file.Name);
                }
            }

            MudDialog.Close(DialogResult.Ok(new NewEntryResult(result!, post, _failedFiles)));
        }
        finally { _saving = false; }
    }
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj --filter "FullyQualifiedName~NewJournalEntryDialogTests"`
Expected: PASS — all 5 tests green.

- [x] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Shared/NewJournalEntryDialog.razor tests/KoalaBooks.ComponentTests/NewJournalEntryDialogTests.cs
git commit -m "Add NewJournalEntryDialog with attach-on-create support"
```

---

## Task 2: Wire `NewJournalEntryDialog` into `Journal.razor`

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Consumes: `NewJournalEntryDialog` (Task 1) — parameters `Accounts`, `FiscalYearId`; `[CascadingParameter]`-driven result type `NewJournalEntryDialog.NewEntryResult { JournalEntry Entry, bool Posted, List<string> FailedFiles }`.
- Produces: nothing new for later tasks (this is the last task in the plan).

- [x] **Step 1: Remove the inline form's markup**

In `src/KoalaBooks.Components/Pages/Journal.razor`, delete line 27:

```razor
<UnsavedChangesGuard IsDirty="_isDirty" />
```

Delete the `@if (_showForm) { ... }` block (lines 50–68):

```razor
@if (_showForm)
{
    <div class="card">
        <h3>Ny verifikation</h3>

        <JournalEntryForm Accounts="_accounts"
                          Date="_formDate" DateChanged="d => _formDate = d"
                          Description="@_formDescription" DescriptionChanged="d => _formDescription = d"
                          Lines="_formLines"
                          IsBalancedChanged="b => _isBalanced = b"
                          DirtyChanged="MarkDirty" />

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveAndPost" disabled="@(!_isBalanced)">💾 Bokför</button>
            <button class="btn btn-secondary" @onclick="SaveAsDraft" disabled="@(!_isBalanced)">Spara som utkast</button>
            <button class="btn btn-secondary" @onclick="CancelForm">Avbryt</button>
        </div>
    </div>
}
```

- [x] **Step 2: Change the "+ Ny verifikation" button and the empty-state guard**

Change line 46 from:

```razor
<button class="btn btn-primary" @onclick="NewEntry">+ Ny verifikation</button>
```

to:

```razor
<button class="btn btn-primary" @onclick="OpenNewEntryDialogAsync">+ Ny verifikation</button>
```

Change line 294 (now shifted up after the deletions in Step 1, but identified by content) from:

```razor
@if (!FilteredEntries.Any() && !_showForm)
```

to:

```razor
@if (!FilteredEntries.Any())
```

- [x] **Step 3: Replace the removed `@code` members**

In the `@code` block, delete these field declarations:

```csharp
    private bool _showForm;
    private DateTime _formDate = DateTime.Today;
    private string _formDescription = "";
    private List<JournalEntryForm.LineModel> _formLines = [];
    private bool _isBalanced;

    // Unsaved-changes guard for the new-entry form; cleared on save, cancel, or fresh open.
    private bool _isDirty;
    private void MarkDirty() => _isDirty = true;
```

Delete these methods entirely: `NewEntry()`, `CancelForm()`, `SaveAndPost()`, `SaveAsDraft()`, `SaveEntryAsync(bool post)`.

Add `OpenNewEntryDialogAsync` in their place:

```csharp
    private async Task OpenNewEntryDialogAsync()
    {
        var parameters = new DialogParameters<NewJournalEntryDialog>
        {
            { x => x.Accounts, _accounts },
            { x => x.FiscalYearId, _activeFiscalYear!.Id }
        };
        var dialogRef = await DialogService.ShowAsync<NewJournalEntryDialog>(
            "Ny verifikation", parameters, DialogDefaults.NoDismiss);
        var result = await dialogRef.Result;
        if (result is { Canceled: false } && result.Data is NewJournalEntryDialog.NewEntryResult r)
        {
            Snackbar.Add(r.Posted
                ? $"Verifikation #{r.Entry.EntryNumber} bokförd."
                : $"Verifikation #{r.Entry.EntryNumber} sparad som utkast.",
                Severity.Success);
            if (r.FailedFiles.Count > 0)
            {
                Snackbar.Add($"Kunde inte bifoga: {string.Join(", ", r.FailedFiles)}. Försök igen via bilaga-panelen.",
                    Severity.Warning);
            }
            await ReloadEntriesAsync();
        }
    }
```

- [x] **Step 4: Remove the now-dead `_showForm`/`_isDirty` resets in `OnFiscalYearChangedAsync`**

In `OnFiscalYearChangedAsync`, remove the two lines:

```csharp
        _showForm = false;
        _isDirty = false;
```

leaving the rest of the method (`SelectionContext.Set(...)`, `_selectedMonthStr = ""`, `_attachmentEntryId = null`, `_attachmentMeta = []`, `_convertingEntryId = null`, `_isReloading = true`, `await LoadForSelectedYearAsync();`, `_isReloading = false;`) unchanged.

- [x] **Step 5: Build and run the full test suite**

Run: `dotnet build`
Expected: builds clean, no leftover references to `_showForm`/`_formDate`/`_formDescription`/`_formLines`/`_isBalanced`/`_isDirty`/`MarkDirty`/`NewEntry`/`CancelForm`/`SaveAndPost`/`SaveAsDraft`/`SaveEntryAsync` anywhere in `Journal.razor`.

Run: `dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj`
Expected: PASS — full component-test suite green, including Task 1's `NewJournalEntryDialogTests`.

- [x] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "Replace Journal.razor's inline new-entry form with NewJournalEntryDialog"
```

- [x] **Step 7: Manual Playwright verification** (controller performs this after Task 2's review passes, not delegated to a subagent — requires a running app and browser interaction)

Against a running dev instance:

- Open dialog via "+ Ny verifikation", create an entry with zero attachments (both "💾 Bokför" and "Spara som utkast" paths) — confirm identical behavior to the old inline form (snackbar text, list reload).
- Create an entry with one and with multiple staged files — confirm all are linked (verify via the 📎 panel's count/list on the resulting row).
- Remove a staged file via its ✕ before submitting — confirm it's not uploaded.
- Force an attachment failure (e.g. a >10 MB file) alongside a valid one — confirm the entry is still created, the valid file is linked, the warning snackbar names only the failed file, and the failed file is retryable via the 📎 panel afterward.
- Confirm backdrop-click/Escape no longer dismiss the dialog (`DialogDefaults.NoDismiss`, matching the other two dialogs).
- Confirm editing the form and attempting to navigate away triggers the unsaved-changes prompt (`UnsavedChangesGuard` inside the dialog).
