namespace KoalaBooks.Application.Services;

public static class TotpUriBuilder
{
    public static string BuildOtpAuthUri(string issuer, string accountName, string unformattedKey)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedAccount = Uri.EscapeDataString(accountName);
        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}?secret={unformattedKey}&issuer={encodedIssuer}&digits=6";
    }
}
