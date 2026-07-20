using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Regression coverage for #283: Todo.razor used to resolve one arbitrary "active"
// fiscal year via IFiscalYearService.GetActiveAsync(), which silently hid unmatched
// bank transactions / unpaid invoices belonging to any other open year and always
// posted new entries into that single year regardless of which item was being
// worked on. It now spans every open fiscal year and scopes accounts/posting to
// each item's own FiscalYearId (see PR #312).
public class TodoTests : BunitContext
{
    private const int OrganisationId = 1;
    private const int FiscalYear2025Id = 1;
    private const int FiscalYear2026Id = 2;

    private readonly IBankImportService _bankImportService = Substitute.For<IBankImportService>();
    private readonly ISupplierInvoiceService _supplierInvoiceService = Substitute.For<ISupplierInvoiceService>();
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();

    public TodoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_bankImportService);
        Services.AddSingleton(_supplierInvoiceService);
        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_accountService);

        _supplierInvoiceService.GetAllForOrganisationAsync().Returns([]);
        _bankImportService.GetUnmatchedForOrganisationAsync().Returns([]);
        _bankImportService.SuggestContraAccountAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<decimal>())
            .Returns((int?)null);
    }

    private static Account MakeAccount(int id, int fiscalYearId, string number, string name) => new()
    {
        Id = id,
        FiscalYearId = fiscalYearId,
        AccountNumber = number,
        Name = name,
    };

    private static BankTransaction MakeUnmatchedTx(int id, int fiscalYearId, string description, DateOnly date)
    {
        var account = MakeAccount(id * 100, fiscalYearId, "1930", "Bank");
        return new BankTransaction
        {
            Id = id,
            OrganisationId = OrganisationId,
            AccountId = account.Id,
            Account = account,
            Date = date,
            Amount = 100m,
            Description = description,
            Status = BankTransactionStatus.Unmatched,
        };
    }

    [Fact]
    public void TwoOpenFiscalYears_ItemsFromBothYearsAppearTogether()
    {
        _bankImportService.GetUnmatchedForOrganisationAsync().Returns([
            MakeUnmatchedTx(1, FiscalYear2025Id, "tx-2025", new DateOnly(2025, 6, 1)),
            MakeUnmatchedTx(2, FiscalYear2026Id, "tx-2026", new DateOnly(2026, 6, 1)),
        ]);

        var cut = Render<Todo>();

        Assert.Contains("tx-2025", cut.Markup);
        Assert.Contains("tx-2026", cut.Markup);
    }

    [Fact]
    public async Task ExpandingItem_LoadsAccountsScopedToItsOwnFiscalYear_NotAnotherOpenYear()
    {
        _bankImportService.GetUnmatchedForOrganisationAsync().Returns([
            MakeUnmatchedTx(1, FiscalYear2025Id, "tx-2025", new DateOnly(2025, 6, 1)),
        ]);
        _accountService.GetAllAsync(FiscalYear2025Id).Returns([
            MakeAccount(10, FiscalYear2025Id, "2440", "Lev.skulder 2025"),
        ]);
        _accountService.GetAllAsync(FiscalYear2026Id).Returns([
            MakeAccount(20, FiscalYear2026Id, "2440", "Lev.skulder 2026"),
        ]);

        var cut = Render<Todo>();
        await cut.InvokeAsync(() => cut.Find("button.btn-success").Click());
        await cut.InvokeAsync(() =>
            cut.Find("input[placeholder='Sök på kontonummer eller namn…']").TriggerEvent("onfocusin", new EventArgs()));

        _ = _accountService.Received(1).GetAllAsync(FiscalYear2025Id);
        _ = _accountService.DidNotReceive().GetAllAsync(FiscalYear2026Id);
        Assert.Contains("Lev.skulder 2025", cut.Markup);
        Assert.DoesNotContain("Lev.skulder 2026", cut.Markup);
    }

    [Fact]
    public async Task PostingBankTxItem_CreatesEntryInTheItemsOwnFiscalYear()
    {
        _bankImportService.GetUnmatchedForOrganisationAsync().Returns([
            MakeUnmatchedTx(1, FiscalYear2026Id, "tx-2026", new DateOnly(2026, 6, 1)),
        ]);
        var contraAccount = MakeAccount(20, FiscalYear2026Id, "6100", "Kontorsmaterial");
        _accountService.GetAllAsync(FiscalYear2026Id).Returns([contraAccount]);

        JournalEntry? created = null;
        _journalEntryService.CreateAsync(Arg.Any<JournalEntry>())
            .Returns(ci =>
            {
                created = ci.Arg<JournalEntry>();
                return ((JournalEntry?)created, (string?)null);
            });
        _bankImportService.MatchToEntryAsync(Arg.Any<int>(), Arg.Any<int>()).Returns((string?)null);

        var cut = Render<Todo>();
        await cut.InvokeAsync(() => cut.Find("button.btn-success").Click());
        await cut.InvokeAsync(() =>
            cut.Find("input[placeholder='Sök på kontonummer eller namn…']").TriggerEvent("onfocusin", new EventArgs()));
        await cut.InvokeAsync(() => cut.Find(".account-dropdown-list li").MouseDown());
        await cut.InvokeAsync(() =>
            cut.FindAll("button").First(b => b.TextContent.Contains("Skapa verifikation")).Click());

        Assert.NotNull(created);
        Assert.Equal(FiscalYear2026Id, created!.FiscalYearId);
        Assert.Contains(created.Lines, l => l.AccountId == contraAccount.Id);
    }
}
