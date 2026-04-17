using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #1: AccountClass mapping tests.
/// BAS kontoplan mapping: 1xxx=Asset, 20xx=Equity, 21xx-29xx=Liability,
/// 3xxx=Revenue, 4xxx-7xxx=Expense, 8xxx=Financial (P&L).
/// Current bug: ALL 2xxx mapped to Liability (20xx should be Equity),
/// and 8xxx mapped to Equity (should be P&L).
/// </summary>
public class AccountClassMapperTests
{
    [Fact]
    public void Account2010_Aktiekapital_ShouldBeEquity()
    {
        var result = AccountClassMapper.FromAccountNumber("2010");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Equity, result);
    }

    [Fact]
    public void Account2081_Aktiekapital_ShouldBeEquity()
    {
        var result = AccountClassMapper.FromAccountNumber("2081");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Equity, result);
    }

    [Fact]
    public void Account2099_LastEquityAccount_ShouldBeEquity()
    {
        var result = AccountClassMapper.FromAccountNumber("2099");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Equity, result);
    }

    [Fact]
    public void Account2440_Leverantorsskulder_ShouldBeLiability()
    {
        var result = AccountClassMapper.FromAccountNumber("2440");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Liability, result);
    }

    [Fact]
    public void Account2100_FirstLiabilityAccount_ShouldBeLiability()
    {
        var result = AccountClassMapper.FromAccountNumber("2100");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Liability, result);
    }

    [Fact]
    public void Account2999_LastLiabilityAccount_ShouldBeLiability()
    {
        var result = AccountClassMapper.FromAccountNumber("2999");

        Assert.NotNull(result);
        Assert.Equal(AccountClass.Liability, result);
    }

    [Fact]
    public void Account8310_Ranteintakter_ShouldNotBeEquity()
    {
        var result = AccountClassMapper.FromAccountNumber("8310");

        Assert.NotNull(result);
        Assert.NotEqual(AccountClass.Equity, result);
    }

    [Fact]
    public void Account8999_ShouldNotBeEquity()
    {
        var result = AccountClassMapper.FromAccountNumber("8999");

        Assert.NotNull(result);
        Assert.NotEqual(AccountClass.Equity, result);
    }

    [Theory]
    [InlineData("1000", AccountClass.Asset)]
    [InlineData("1910", AccountClass.Asset)]
    [InlineData("1999", AccountClass.Asset)]
    [InlineData("3010", AccountClass.Revenue)]
    [InlineData("3999", AccountClass.Revenue)]
    [InlineData("4010", AccountClass.Expense)]
    [InlineData("5010", AccountClass.Expense)]
    [InlineData("6010", AccountClass.Expense)]
    [InlineData("7010", AccountClass.Expense)]
    [InlineData("7999", AccountClass.Expense)]
    public void StandardAccountClasses_MapCorrectly(string accountNumber, AccountClass expected)
    {
        var result = AccountClassMapper.FromAccountNumber(accountNumber);

        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2010", AccountClass.Equity)]
    [InlineData("2081", AccountClass.Equity)]
    [InlineData("2099", AccountClass.Equity)]
    public void Class20xx_ShouldBeEquity(string accountNumber, AccountClass expected)
    {
        var result = AccountClassMapper.FromAccountNumber(accountNumber);

        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2100", AccountClass.Liability)]
    [InlineData("2440", AccountClass.Liability)]
    [InlineData("2999", AccountClass.Liability)]
    public void Class21xx_To_29xx_ShouldBeLiability(string accountNumber, AccountClass expected)
    {
        var result = AccountClassMapper.FromAccountNumber(accountNumber);

        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EmptyAccountNumber_ReturnsNull()
    {
        var result = AccountClassMapper.FromAccountNumber("");
        Assert.Null(result);
    }

    [Fact]
    public void NonDigitFirstChar_ReturnsNull()
    {
        var result = AccountClassMapper.FromAccountNumber("ABC");
        Assert.Null(result);
    }

    [Fact]
    public void Account9xxx_ReturnsNull()
    {
        var result = AccountClassMapper.FromAccountNumber("9999");
        Assert.Null(result);
    }
}
