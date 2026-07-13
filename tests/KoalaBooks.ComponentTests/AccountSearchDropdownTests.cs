using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.AspNetCore.Components.Web;

namespace KoalaBooks.ComponentTests;

public class AccountSearchDropdownTests : BunitContext
{
    private static List<Account> SampleAccounts() =>
    [
        new() { Id = 1, AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset },
        new() { Id = 2, AccountNumber = "2440", Name = "Leverantörsskulder", AccountClass = AccountClass.Liability },
        new() { Id = 3, AccountNumber = "3010", Name = "Försäljning", AccountClass = AccountClass.Revenue },
    ];

    [Fact]
    public void FocusIn_WithNoSelection_OpensDropdownWithAllAccounts()
    {
        var cut = Render<AccountSearchDropdown>(p => p.Add(x => x.Accounts, SampleAccounts()));

        cut.Find("input").TriggerEvent("onfocusin", new EventArgs());

        Assert.Equal(3, cut.FindAll(".account-dropdown-list li").Count);
    }

    [Fact]
    public void Typing_FiltersAccountsByNumberOrName()
    {
        var cut = Render<AccountSearchDropdown>(p => p.Add(x => x.Accounts, SampleAccounts()));
        cut.Find("input").TriggerEvent("onfocusin", new EventArgs());

        cut.Find("input").Input("kassa");

        var items = cut.FindAll(".account-dropdown-list li");
        Assert.Single(items);
        Assert.Contains("Kassa", items[0].TextContent);
    }

    [Fact]
    public void Typing_NoMatches_ShowsNoResultsMessage()
    {
        var cut = Render<AccountSearchDropdown>(p => p.Add(x => x.Accounts, SampleAccounts()));
        cut.Find("input").TriggerEvent("onfocusin", new EventArgs());

        cut.Find("input").Input("zzz-no-such-account");

        var item = cut.Find(".account-dropdown-list li.no-results");
        Assert.Equal("Inga matchande konton", item.TextContent);
    }

    [Fact]
    public void ClickingAccount_SelectsIt_AndInvokesSelectedAccountIdChanged()
    {
        int? selectedId = null;
        var cut = Render<AccountSearchDropdown>(p => p
            .Add(x => x.Accounts, SampleAccounts())
            .Add(x => x.SelectedAccountIdChanged, id => selectedId = id));
        cut.Find("input").TriggerEvent("onfocusin", new EventArgs());

        cut.Find(".account-dropdown-list li").MouseDown();

        Assert.Equal(1, selectedId);
        Assert.Equal("1910 — Kassa", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void ArrowDownThenEnter_SelectsHighlightedAccount()
    {
        int? selectedId = null;
        var cut = Render<AccountSearchDropdown>(p => p
            .Add(x => x.Accounts, SampleAccounts())
            .Add(x => x.SelectedAccountIdChanged, id => selectedId = id));
        cut.Find("input").TriggerEvent("onfocusin", new EventArgs());

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(1, selectedId);
    }

    [Fact]
    public void PreselectedAccount_ShowsFormattedTextOnLoad()
    {
        var cut = Render<AccountSearchDropdown>(p => p
            .Add(x => x.Accounts, SampleAccounts())
            .Add(x => x.SelectedAccountId, 2));

        Assert.Equal("2440 — Leverantörsskulder", cut.Find("input").GetAttribute("value"));
    }
}
