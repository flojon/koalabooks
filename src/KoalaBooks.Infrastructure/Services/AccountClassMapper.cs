using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Infrastructure.Services;

/// <summary>
/// Maps BAS account numbers to account classes.
/// BAS standard: 1=Asset, 20xx=Equity, 21xx-29xx=Liability, 3=Revenue,
/// 4-7=Expense, 80xx-83xx=Financial revenue, 84xx-89xx=Financial expense.
/// </summary>
public static class AccountClassMapper
{
    public static AccountClass? FromAccountNumber(string accountNumber)
    {
        if (accountNumber.Length == 0 || !char.IsDigit(accountNumber[0]))
            return null;

        return accountNumber[0] switch
        {
            '1' => AccountClass.Asset,
            '2' => MapClass2(accountNumber),
            '3' => AccountClass.Revenue,
            >= '4' and <= '7' => AccountClass.Expense,
            '8' => MapClass8(accountNumber),
            _ => null
        };
    }

    // 2000-2099 = Equity (Eget kapital), 2100-2999 = Liability (Skulder)
    private static AccountClass MapClass2(string accountNumber)
    {
        if (accountNumber.Length >= 2 && accountNumber[1] == '0')
            return AccountClass.Equity;
        return AccountClass.Liability;
    }

    // 8000-8399 = Financial revenue, 8400-8999 = Financial expense
    private static AccountClass MapClass8(string accountNumber)
    {
        if (accountNumber.Length >= 2 && accountNumber[1] < '4')
            return AccountClass.Revenue;
        return AccountClass.Expense;
    }
}
