using System.Text;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace KoalaBooks.Application.Services;

public class MfaService : IMfaService
{
    private const string Issuer = "KoalaBooks";
    private readonly UserManager<ApplicationUser> _userManager;

    public MfaService(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<MfaEnrollmentInfo> BeginEnrollmentAsync(ApplicationUser user)
    {
        await _userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        var unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Authenticator key was not generated.");

        var accountName = user.Email ?? user.UserName ?? "user";
        var otpAuthUri = TotpUriBuilder.BuildOtpAuthUri(Issuer, accountName, unformattedKey);
        var qrCodeDataUri = QrCodeGenerator.GenerateDataUri(otpAuthUri);

        return new MfaEnrollmentInfo(FormatKey(unformattedKey), qrCodeDataUri);
    }

    public async Task<MfaConfirmResult> ConfirmEnrollmentAsync(ApplicationUser user, string code)
    {
        var normalized = code.Replace(" ", "").Replace("-", "");
        var isValid = await _userManager.VerifyTwoFactorTokenAsync(
            user, TokenOptions.DefaultAuthenticatorProvider, normalized).ConfigureAwait(false);

        if (!isValid)
            return new MfaConfirmResult(false, "Fel kod. Kontrollera att klockan i din autentiseringsapp är rätt inställd och försök igen.", []);

        await _userManager.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10).ConfigureAwait(false);

        return new MfaConfirmResult(true, null, (codes ?? []).ToArray());
    }

    public async Task<bool> DisableAsync(ApplicationUser user, string password)
    {
        if (!await _userManager.CheckPasswordAsync(user, password).ConfigureAwait(false))
            return false;

        await _userManager.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
        await _userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        return true;
    }

    private static string FormatKey(string unformattedKey)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < unformattedKey.Length; i += 4)
        {
            sb.Append(unformattedKey.AsSpan(i, Math.Min(4, unformattedKey.Length - i)));
            sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }
}
