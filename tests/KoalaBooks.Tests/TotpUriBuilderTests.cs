using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class TotpUriBuilderTests
{
    [Fact]
    public void BuildOtpAuthUri_EncodesIssuerAccountAndSecret()
    {
        var uri = TotpUriBuilder.BuildOtpAuthUri("KoalaBooks", "user@example.com", "JBSWY3DPEHPK3PXP");

        Assert.Equal(
            "otpauth://totp/KoalaBooks:user%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=KoalaBooks&digits=6",
            uri);
    }

    [Fact]
    public void BuildOtpAuthUri_EscapesSpecialCharactersInAccountName()
    {
        var uri = TotpUriBuilder.BuildOtpAuthUri("Koala Books", "a b+c@example.com", "SECRET");

        Assert.Equal(
            "otpauth://totp/Koala%20Books:a%20b%2Bc%40example.com?secret=SECRET&issuer=Koala%20Books&digits=6",
            uri);
    }
}
