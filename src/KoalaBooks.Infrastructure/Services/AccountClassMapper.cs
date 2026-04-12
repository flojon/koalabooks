using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Infrastructure.Services;

/// <summary>
/// Maps BAS account numbers to account classes based on the first digit.
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
            '2' => AccountClass.Liability,
            '3' => AccountClass.Revenue,
            >= '4' and <= '7' => AccountClass.Expense,
            '8' => AccountClass.Equity,
            _ => null
        };
    }
}
