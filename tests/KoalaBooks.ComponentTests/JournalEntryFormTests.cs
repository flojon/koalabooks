using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

public class JournalEntryFormTests : BunitContext
{
    public JournalEntryFormTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    private static List<Account> MakeAccounts() =>
    [
        new() { Id = 1, AccountNumber = "1930", Name = "Företagskonto", IsActive = true },
        new() { Id = 2, AccountNumber = "4010", Name = "Inköp material", IsActive = true },
    ];

    [Fact]
    public void ExtraCompletelyEmptyLine_DoesNotBlockBalance()
    {
        var lines = new List<JournalEntryForm.LineModel>
        {
            new() { AccountId = 1, DebitAmount = 100 },
            new() { AccountId = 2, CreditAmount = 100 },
            new(),
        };
        bool? isBalanced = null;

        Render<JournalEntryForm>(p => p
            .Add(x => x.Accounts, MakeAccounts())
            .Add(x => x.Lines, lines)
            .Add(x => x.IsBalancedChanged, EventCallback.Factory.Create<bool>(this, b => isBalanced = b)));

        Assert.True(isBalanced);
    }

    [Fact]
    public void LineWithAmountButNoAccount_StillBlocksBalance()
    {
        var lines = new List<JournalEntryForm.LineModel>
        {
            new() { AccountId = 1, DebitAmount = 100 },
            new() { CreditAmount = 100 },
        };
        bool? isBalanced = null;

        var comp = Render<JournalEntryForm>(p => p
            .Add(x => x.Accounts, MakeAccounts())
            .Add(x => x.Lines, lines)
            .Add(x => x.IsBalancedChanged, EventCallback.Factory.Create<bool>(this, b => isBalanced = b)));

        Assert.False(isBalanced);
        Assert.Contains("Välj konto på alla rader.", comp.Markup);
    }
}
