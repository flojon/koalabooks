namespace KoalaBooks.Domain.Enums;

public enum AccountClass
{
    Asset = 1,
    Liability = 2,
    Revenue = 3,
    Expense = 4,
    Equity = 8
}

public static class AccountClassExtensions
{
    /// <summary>
    /// Credit-normal accounts increase with credits: Liability, Equity, Revenue.
    /// Debit-normal accounts increase with debits: Asset, Expense.
    /// </summary>
    public static bool IsCreditNormal(this AccountClass accountClass)
        => accountClass is AccountClass.Liability or AccountClass.Equity or AccountClass.Revenue;
}
